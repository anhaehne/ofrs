﻿using System;
using BenchmarkDotNet.Running;
using Clustering.BenchmarkDotNet.Algorithms;

namespace Clustering.BenchmarkDotNet
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            var b = new Benchmark();
            b.Setup();
            b.Algorithm = new ChineseWhispersV4SelfOptimizing();
            b.RunAlgorithm();

            //var summary = BenchmarkRunner.Run<Benchmark>();//new DebugInProcessConfig());
            Console.ReadLine();
        }
    }
}