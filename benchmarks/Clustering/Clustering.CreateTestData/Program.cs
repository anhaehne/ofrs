using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DlibDotNet;
using DlibDotNet.Dnn;
using MessagePack;

namespace Clustering.CreateTestData
{
    /// <summary>
    /// Preparing https://cswww.essex.ac.uk/mv/allfaces/faces96.htmlfor testing.
    /// </summary>
    internal class Program
    {
        private static void Main(string[] args)
        {
            var _subjectData = new ConcurrentDictionary<string, ConcurrentBag<float[]>>();

            var dataset = Directory.EnumerateDirectories(args.First())
                .SelectMany(x => Directory.EnumerateFiles(x, "*.jpg").Select(z => (Id: x, Image: z)))
                .ToList();

            var done = 0;
            var detectors = new ObjectPool<Detector>(() => new Detector(args.First()));

            Parallel.ForEach(dataset, data =>
            {
                var detector = detectors.GetObject();
                using var img = Dlib.LoadImageAsMatrix<RgbPixel>(Path.Combine(args.First(), "images", data.Image));

                // Run the face detector on the image of our action heroes, and for each face extract a
                // copy that has been normalized to 150x150 pixels in size and appropriately rotated
                // and centered.
                var faces = detector.FrontalFaceDetector.Operator(img);

                if (faces.Length != 1)
                {
                    Console.WriteLine($"Image {data.Image} contained {faces.Length} faces. Subject will be ignored.");
                    detectors.PutObject(detector);
                    return;
                }

                var face = faces.Single();
                using var shape = detector.ShapePredictor.Detect(img, face);
                using var faceChipDetail = Dlib.GetFaceChipDetails(shape, 150, 0.25);
                using var faceChip = Dlib.ExtractImageChip<RgbPixel>(img, faceChipDetail);

                using var encoding = detector.LossMetric.Operator(faceChip).Single();

                detectors.PutObject(detector);

                var rawEncoding = encoding.ToArray();

                _subjectData.GetOrAdd(data.Id, i => new ConcurrentBag<float[]>()).Add(rawEncoding);

                Interlocked.Increment(ref done);
                Console.WriteLine($"Done {done}/{dataset.Count}");
            });

            var result = _subjectData.Select(x => x.Value.ToList()).ToList();

            var bytes = MessagePackSerializer.Serialize(result);
            File.WriteAllBytes(Path.Combine(args.First(), "data.msgpck"), bytes);
        }

        private class Detector
        {
            public Detector(string path)
            {
                FrontalFaceDetector = Dlib.GetFrontalFaceDetector();
                ShapePredictor = ShapePredictor.Deserialize(Path.Combine(path, "shape_predictor_5_face_landmarks.dat"));
                LossMetric = LossMetric.Deserialize(Path.Combine(path, "dlib_face_recognition_resnet_model_v1.dat"));
            }

            public FrontalFaceDetector FrontalFaceDetector { get; }

            public ShapePredictor ShapePredictor { get; }

            public LossMetric LossMetric { get; }
        }
    }
}