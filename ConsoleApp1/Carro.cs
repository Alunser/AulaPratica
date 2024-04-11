namespace ConsoleApp1
{
    public class Carro : Veiculo
    {
        #region Construtor

        public Carro(string marca, string modelo)
        {
            Marca = marca;
            Modelo = modelo;
        }


        #endregion

        #region Propriedades



        #endregion

        #region Método

        public override string VelocidadeMaxima(int velocidade)
        {
            var velocidadeFinal = velocidade * 2;



            return base.VelocidadeMaxima(velocidadeFinal);
        }

        #endregion
    }
}
