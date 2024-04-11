namespace ConsoleApp1
{
    public abstract class Veiculo : IFabrica
    {
        #region Construtor

        public Veiculo()
        {
            Identificador = Guid.NewGuid();
            DataCriacao = DateTime.Now;
        }

        #endregion

        #region Propriedades

        public Guid Identificador { get; set; }
        public string Marca { get; set; }
        public string Modelo { get; set; }
        private DateTime DataCriacao { get; set; }






        #endregion

        #region Métodos

        public virtual string VelocidadeMaxima(int velocidade) 
        {
            return $"A velocidade máxima do carro é {velocidade} KMPH";
        }
        

        #endregion
    }
}
