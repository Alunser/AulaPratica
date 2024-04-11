// See https://aka.ms/new-console-template for more information
using ConsoleApp1;

Console.WriteLine("Hello, World!");

var carro = new Carro("Honda", "Civic");
var moto = new Moto("Honda", "CG");

Console.WriteLine(carro.VelocidadeMaxima(180));
Console.WriteLine(moto.VelocidadeMaxima(120));

Console.ReadKey();


//carro.Id = Guid.NewGuid();

