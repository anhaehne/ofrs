using Clustering.BenchmarkDotNet.Algorithms;
using System;

namespace Clustering.BenchmarkDotNet
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            Benchmark b = new Benchmark();
            b.Setup();
            b.Algorithm = new ChineseWhispersV6LockFree(0.4);
            b.RunAlgorithm();

            //var summary = BenchmarkRunner.Run<Benchmark>();//new DebugInProcessConfig());
            Console.ReadLine();
        }
    }
}