using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp1
{
    public class Moto : Veiculo
    {
        #region Construtor

        public Moto(string marca, string modelo)
        {
            Marca = marca;
            Modelo = modelo;
        }



        #endregion

        #region Propriedades


        #endregion

        #region Métodos

        public override string VelocidadeMaxima(int velocidade)
        {
            return base.VelocidadeMaxima(velocidade);
        }

        #endregion
    }
}
