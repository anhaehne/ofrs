using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Clustering.BenchmarkDotNet.Algorithms;
using MessagePack;

namespace Clustering.BenchmarkDotNet
{
    [SimpleJob]
    [MinColumn]
    [MaxColumn]
    [MeanColumn]
    [MedianColumn]
    public class Benchmark
    {
        private readonly List<Specimen> _fill = new List<Specimen>();
        private readonly List<Specimen> _test = new List<Specimen>();
        private List<List<float[]>> _encoding;

        [ParamsSource(nameof(Algorithms))]
        public IClusteringAlgorithm Algorithm { get; set; }

        public IEnumerable<IClusteringAlgorithm> Algorithms =>
            new IClusteringAlgorithm[] {new ChineseWhispersV5SimpleLimit(0.4), };

        [GlobalSetup]
        public void Setup()
        {
            var bytes = File.ReadAllBytes("data.msgpck");
            _encoding = MessagePackSerializer.Deserialize<List<List<float[]>>>(bytes).Where(x => x.Count > 2).ToList();

            for (var subjectIndex = 0; subjectIndex < _encoding.Count; subjectIndex++)
            for (var imageIndex = 0; imageIndex < Math.Ceiling(_encoding[subjectIndex].Count / 2d); imageIndex++)
                _fill.Add(new Specimen
                {
                    Encoding = _encoding[subjectIndex][imageIndex],
                    SubjectIndex = subjectIndex
                });

            for (var subjectIndex = 0; subjectIndex < _encoding.Count; subjectIndex++)
            for (var imageIndex = (int) Math.Ceiling(_encoding[subjectIndex].Count / 2d);
                imageIndex < _encoding[subjectIndex].Count;
                imageIndex++)
                _test.Add(new Specimen
                {
                    Encoding = _encoding[subjectIndex][imageIndex],
                    SubjectIndex = subjectIndex
                });

            Randomize(_fill);
            Randomize(_test);
        }

        [Benchmark]
        public void RunAlgorithm()
        {
            var allClusterIds = new HashSet<Guid>();
            var results = new HashSet<Guid>[_encoding.Count];

            for (var i = 0; i < _encoding.Count; i++)
                results[i] = new HashSet<Guid>();


            Console.WriteLine("Starting fill");

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var random = new Random(1234);
            float[] temp = new float[128];

            foreach (var specimen in _fill)
            {
                var clusterId = Algorithm.GetCluster(specimen.Encoding);
                allClusterIds.Add(clusterId);
                results[specimen.SubjectIndex].Add(clusterId);
            }

            // Add some noise
            Parallel.For(0, 100, i =>
            {
                foreach (var specimen in _fill)
                {
                    var t = random.Next(-500, 500) * 0.0000000001f;

                    for (int j = 0; j < 128; j++)
                    {
                        temp[j] = specimen.Encoding[j] + t;
                    }

                    var clusterId = Algorithm.GetCluster(temp);
                    allClusterIds.Add(clusterId);
                    results[specimen.SubjectIndex].Add(clusterId);
                }
            });

            stopwatch.Stop();

            Console.WriteLine($"Filling took {stopwatch.ElapsedMilliseconds}ms");
            
            stopwatch.Reset();
            stopwatch.Start();

            var wrongCategory = 0;
            var unknown = 0;

            foreach (var specimen in _test)
            {
                var clusterId = Algorithm.GetCluster(specimen.Encoding);

                if (!allClusterIds.Contains(clusterId))
                {
                    unknown++;
                    results[specimen.SubjectIndex].Add(clusterId);
                }
                else if (!results[specimen.SubjectIndex].Contains(clusterId))
                    wrongCategory++;

            }

            stopwatch.Stop();
            Console.WriteLine($"Testing took {stopwatch.ElapsedMilliseconds}ms for {_test.Count} specimen ({1000 / (stopwatch.ElapsedMilliseconds / (double)_test.Count):F} ops).");

            Console.WriteLine(
                $"There are {Algorithm.ClusterCount} distinct clusters (max size {Algorithm.MaxClusterSize}) for {_test.Count + _fill.Count} images of {_encoding.Count} subjects.");

            Console.WriteLine(
                $"{wrongCategory + unknown} predictions have been false. {wrongCategory} wrong categorization and {unknown} new categorization. Correctness: {1 - (wrongCategory + unknown) / (double)_test.Count:P}");
        }

        private void Randomize<T>(List<T> items)
        {
            var rand = new Random();
            var n = items.Count;

            while (n > 1)
            {
                n--;
                var k = rand.Next(n + 1);
                var value = items[k];
                items[k] = items[n];
                items[n] = value;
            }
        }

        private class Specimen
        {
            public int SubjectIndex { get; set; }

            public float[] Encoding { get; set; }
        }
    }
}