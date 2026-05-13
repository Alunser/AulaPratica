// ============================================================
//  ProcessarMovimentoContabilWorkerHandler — VERSÃO REFATORADA
//  Princípio: cada método tem UMA responsabilidade clara.
//
//  FLUXO:
//  Handle
//    └─ Consome lote do Kafka
//    └─ Parallel.ForEachAsync → ProcessarMensagemIndividual
//          ├─ Mapear entidade
//          ├─ Validar campos
//          ├─ Validar empresa
//          ├─ Validar evento de negócio
//          ├─ Verificar duplicidade
//          └─ GravarERetornarRetorno(movimento, sucesso, mensagemErro?)
//               ├─ [NOK] → InserirMovimentoErro + retorno com status 422
//               └─ [OK]  → InserirMovimentoContabil + retorno com status 200
//    └─ PublicarRetornos(todosOsRetornos)
//    └─ Commit ou rollback
//
// ============================================================
//  REGRA CRÍTICA — COMMIT vs REENVIO KAFKA
//
//  Existem DOIS tipos de falha com comportamentos completamente diferentes:
//
//  1. ERRO DE NEGÓCIO (validação, empresa inválida, evento não encontrado...)
//     → A mensagem FOI processada, só que com resultado negativo.
//     → Grava na tabela de erros + publica retorno 422.
//     → Commit DEVE acontecer normalmente após a publicação.
//     → NÃO deve voltar pro Kafka.
//     ⚠️ Se voltar pro Kafka vai falhar eternamente — LOOP INFINITO.
//
//  2. ERRO TRANSIENTE (exceção inesperada, banco fora, timeout, falha na publicação)
//     → A mensagem NÃO foi processada. Algo inesperado quebrou no meio.
//     → NÃO faz commit. O Kafka reenvia o lote automaticamente.
//     → Correto — o problema pode se resolver sozinho.
//
//  RESUMO:
//  Erro de negócio      → commit ✅    (mensagem tratada, mesmo que com erro)
//  Erro transiente      → sem commit ✅ (Kafka reenvia)
//  Falha na publicação  → sem commit ✅ (Kafka reenvia o lote inteiro)
//
//  ⚠️ NUNCA bloqueie o commit por causa de erro de validação/negócio.
// ============================================================

public class ProcessarMovimentoContabilWorkerHandler
    : IRequestHandler<ProcessarMovimentoContabilWorkerCommand, ICommandResult>
{
    private readonly IRecebimentoMovimentoContabilService _recebimentoService;
    private readonly IRetornoMovimentoContabilService     _retornoService;
    private readonly IMovimentoContabilRepository         _repository;
    private readonly ILogger<ProcessarMovimentoContabilWorkerHandler> _logger;
    private readonly WorkerConfiguration                  _workerConfiguration;

    // Cache de empresa thread-safe mantido por instância do handler.
    // Evita consultas repetidas ao banco para a mesma empresa dentro do mesmo lote.
    private readonly ConcurrentDictionary<string, string> _cacheEmpresa = new();

    public ProcessarMovimentoContabilWorkerHandler(
        IRecebimentoMovimentoContabilService recebimentoService,
        IRetornoMovimentoContabilService retornoService,
        IMovimentoContabilRepository repository,
        ILogger<ProcessarMovimentoContabilWorkerHandler> logger,
        WorkerConfiguration workerConfiguration)
    {
        _recebimentoService  = recebimentoService;
        _retornoService      = retornoService;
        _repository          = repository;
        _logger              = logger;
        _workerConfiguration = workerConfiguration;
    }

    // =========================================================
    //  PONTO DE ENTRADA
    //  Responsabilidade: orquestrar o lote. Não valida, não grava, não publica.
    // =========================================================
    public async Task<ICommandResult> Handle(
        ProcessarMovimentoContabilWorkerCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Consome lote do Kafka
        var mensagens = await _recebimentoService
            .ConsumirMensagensEmLote(_workerConfiguration.BatchSize);

        if (!mensagens.Any())
            return new CommandResult((int)HttpStatusCode.NoContent);

        _logger.LogInformation(
            "Iniciando processamento de {Count} movimentos contábeis em lote.",
            mensagens.Count);

        // 2. Processa cada mensagem em paralelo e coleta retornos.
        //    O paralelismo é mantido para garantir performance no volume do Kafka.
        var retornos = new ConcurrentBag<RetornoMovimentoContabil>();

        await Parallel.ForEachAsync(
            mensagens,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _workerConfiguration.MaxParallelismo,
                CancellationToken      = cancellationToken
            },
            async (mensagem, ct) =>
            {
                // ⚠️ INTENCIONAL: exceções NÃO são capturadas aqui.
                //
                // Erros de NEGÓCIO (validação, empresa inválida, etc.) são tratados
                // dentro de ProcessarMensagemIndividual e retornam retorno 422 — nunca jogam exceção.
                //
                // Só estoura exceção em erro TRANSIENTE (banco fora, timeout, etc.).
                // Nesse caso o Parallel cancela o lote e o Kafka reenvia. Comportamento correto.
                var retorno = await ProcessarMensagemIndividual(mensagem);
                retornos.Add(retorno);
            });

        // 3. Publica todos os retornos de uma vez — sucessos e erros de negócio juntos.
        var publicacaoOk = await PublicarRetornos(retornos.ToList());

        // 4. Commit só se a publicação funcionou.
        //
        //    ⚠️ O commit NÃO é bloqueado por erros de negócio (retornos 422).
        //    Erros de negócio já foram gravados na tabela de erros e publicados.
        //    A mensagem foi TRATADA — não deve voltar pro Kafka.
        //
        //    O commit só é bloqueado se a PUBLICAÇÃO falhar (erro transiente).
        //    Nesse caso o Kafka reenvia o lote inteiro para reprocessamento.
        if (!publicacaoOk)
        {
            _logger.LogWarning(
                "Commit não realizado. Falha ao publicar retornos no Kafka. Mensagens serão reencaminhadas.");
            return new CommandResult((int)HttpStatusCode.OK);
        }

        await _recebimentoService.Commit();

        _logger.LogInformation("Processamento do lote finalizado com sucesso.");
        return new CommandResult((int)HttpStatusCode.OK);
    }

    // =========================================================
    //  PROCESSA UMA MENSAGEM INDIVIDUAL
    //  Responsabilidade: validar e retornar um RetornoMovimentoContabil.
    //  NÃO publica, NÃO faz commit, NÃO conhece o lote.
    //
    //  Retorna null em dois casos que devem ser IGNORADOS silenciosamente:
    //   - Mapeamento nulo (mensagem malformada)
    //   - Duplicata já processada (idempotência)
    //  Nesses casos não grava nada e não publica nada.
    // =========================================================
    private async Task<RetornoMovimentoContabil> ProcessarMensagemIndividual(
        RacabimentoMovimentoContabilCommandResult mensagem)
    {
        // ---------- Mapeamento ----------
        var movimento = RacabimentoMovimentoContabil.MapearParaEntidade(mensagem);

        if (movimento is null)
        {
            // Mensagem malformada — descarta sem gravar e sem publicar retorno.
            _logger.LogWarning("Mapeamento retornou nulo. Mensagem descartada.");
            return null;
        }

        // ---------- Validação de campos obrigatórios ----------
        // ERRO DE NEGÓCIO: grava na tabela de erros + retorna 422.
        // O commit acontecerá normalmente — não volta pro Kafka.
        movimento.Validar();

        if (!movimento.EhValido)
        {
            var erroValidacao = movimento.ObterMensagemErros();
            _logger.LogWarning("Campos obrigatórios faltando: {Erros}", erroValidacao);
            return await GravarERetornarRetorno(movimento, sucesso: false, erroValidacao);
        }

        // ---------- Validação de empresa ----------
        // Cache evita consultas repetidas ao banco para a mesma empresa no lote.
        if (!_cacheEmpresa.TryGetValue(movimento.CodigoEmpresa, out var codigoIdentificacaoPessoaEmpresa))
        {
            codigoIdentificacaoPessoaEmpresa = await _repository
                .ConsultarCodigoIdentificacaoPessoaEmpresaAsync(movimento.CodigoEmpresa);

            _cacheEmpresa.TryAdd(movimento.CodigoEmpresa, codigoIdentificacaoPessoaEmpresa);
        }

        if (string.IsNullOrWhiteSpace(codigoIdentificacaoPessoaEmpresa))
        {
            var erroEmpresa = $"Empresa inválida. CodigoEmpresa: {movimento.CodigoEmpresa}";
            _logger.LogWarning(erroEmpresa);
            return await GravarERetornarRetorno(movimento, sucesso: false, erroEmpresa);
        }

        // ---------- Validação de evento de negócio ----------
        var eventoNegocio = await _repository
            .ConsultarEventoNegocioAsync(movimento.IdentificadorEventoNegocio);

        if (eventoNegocio is null)
        {
            var erroEvento =
                $"Evento de negócio não encontrado. IdentificadorEventoNegocio: {movimento.IdentificadorEventoNegocio}";
            _logger.LogWarning(erroEvento);
            return await GravarERetornarRetorno(movimento, sucesso: false, erroEvento);
        }

        eventoNegocio.Validar();

        if (!eventoNegocio.EhValido)
        {
            var erroEvento = eventoNegocio.ObterMensagemErros();
            _logger.LogWarning(erroEvento);
            return await GravarERetornarRetorno(movimento, sucesso: false, erroEvento);
        }

        // ---------- Verificação de duplicidade ----------
        // Mensagem já processada = ignora silenciosamente.
        // Não grava erro, não publica retorno. Idempotência garantida.
        var movimentoExistente = await _repository
            .VerificarMovimentoContabilExistenteAsync(
                movimento.CodigoIdentificacaoMovimentacaoFinanceira);

        if (movimentoExistente)
        {
            _logger.LogWarning(
                "Evento já processado. CodigoIdentificacaoMovimentacaoFinanceira: {Codigo}. Ignorado sem reprocessamento.",
                movimento.CodigoIdentificacaoMovimentacaoFinanceira);
            return null;
        }

        // ---------- Caminho feliz ----------
        return await GravarERetornarRetorno(movimento, sucesso: true);
    }

    // =========================================================
    //  ÚNICO MÉTODO DE GRAVAÇÃO E RETORNO
    //
    //  sucesso=true  → grava tabela de movimentos → retorno 200
    //  sucesso=false → grava tabela de erros      → retorno 422
    //
    //  ⚠️ Ambos os casos resultam em commit no Handle.
    //  A distinção sucesso/erro é de NEGÓCIO, não de infraestrutura.
    // =========================================================
    private async Task<RetornoMovimentoContabil> GravarERetornarRetorno(
        MovimentoContabil movimento,
        bool sucesso,
        string mensagemErro = null)
    {
        if (sucesso)
        {
            var linhasAfetadas = await _repository.InserirMovimentoContabilAsync(movimento);

            if (linhasAfetadas == 0)
            {
                // INSERT IGNORE: duplicata detectada na camada de persistência.
                // Ignora silenciosamente — sem publicar retorno.
                _logger.LogWarning(
                    "Duplicata detectada via INSERT IGNORE. CodigoIdentificacaoMovimentacaoFinanceira: {Codigo}.",
                    movimento.CodigoIdentificacaoMovimentacaoFinanceira);
                return null;
            }

            return MontarRetorno(movimento, HttpStatusCode.OK, "Processado com sucesso");
        }

        // NOK: grava na tabela de erros.
        // Isso é erro de NEGÓCIO — o commit ocorre normalmente depois.
        var movimentoErro = new MovimentoErro
        {
            CodigoAplicacaoOrigem                     = $"FXOP1050{Guid.NewGuid():N}"[..12],
            CodigoIdentificacaoMovimentacaoFinanceira = movimento.CodigoIdentificacaoMovimentacaoFinanceira,
            DescricaoMensagemErro                     = mensagemErro,
            DescricaoLogErro                          = mensagemErro,
            NumeroGrupoEanProdutoConsorcio            = movimento.NumeroGrupoEanProdutoConsorcio,
            NumeroCotaConsorcio                       = movimento.NumeroCotaConsorcio,
            NumeroSequencialVersao                    = movimento.NumeroSequencialVersao,
            IdentificadorGrupo                        = movimento.IdentificadorGrupo,
            NumeroIdentificadorCotaCliente            = movimento.NumeroIdentificadorCotaCliente,
            CodigoUnicoLote                           = movimento.CodigoUnicoTransacaoOrigem,
            DataOperacaoOrigem                        = movimento.DataOperacaoOrigem,
            DataHoraReferenciaPersistencia            = DateTime.Now,
            DataHoraRegistroErro                      = DateTime.Now
        };

        await _repository.InserirMovimentoErroAsync(movimentoErro);

        return MontarRetorno(movimento, HttpStatusCode.UnprocessableEntity, mensagemErro);
    }

    // =========================================================
    //  HELPER: monta o objeto de retorno sem tocar no banco.
    //  Static pois não depende de nenhum estado da instância.
    // =========================================================
    private static RetornoMovimentoContabil MontarRetorno(
        MovimentoContabil movimento,
        HttpStatusCode status,
        string mensagem) =>
        new()
        {
            CodigoIdentificacaoMovimentacaoFinanceira = movimento.CodigoIdentificacaoMovimentacaoFinanceira,
            CodigoIdentificadorReferenciaMovimento    = movimento.CodigoIdentificadorReferenciaMovimento,
            CodigoStatusMovimento                     = ((int)status).ToString(),
            TextoMensagem                             = mensagem
        };

    // =========================================================
    //  PUBLICA LOTE DE RETORNOS — sucessos e erros de negócio juntos.
    //  Retorna false APENAS se a publicação em si falhar (erro transiente).
    //  Retornos 422 (erro de negócio) são publicados normalmente — não bloqueiam commit.
    // =========================================================
    private async Task<bool> PublicarRetornos(List<RetornoMovimentoContabil> retornos)
    {
        // Nulos = duplicatas e mapeamentos inválidos ignorados — não entram na publicação.
        var retornosValidos = retornos
            .Where(r => r is not null)
            .ToList();

        if (!retornosValidos.Any())
        {
            _logger.LogInformation("Nenhum retorno a publicar neste lote.");
            return true;
        }

        var resultado = await _retornoService.PublicarEmLote(retornosValidos);

        if (!resultado.Success)
        {
            // ⚠️ Falha na publicação = erro TRANSIENTE.
            // Retorna false para que o Handle não faça commit.
            // O Kafka reenviará o lote inteiro para reprocessamento.
            _logger.LogError(
                "Erro ao publicar retornos em lote no Kafka. Commit não realizado. Lote será reenviado.");
            return false;
        }

        return true;
    }
}
