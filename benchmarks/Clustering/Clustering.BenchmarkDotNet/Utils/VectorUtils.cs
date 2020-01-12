using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Clustering.BenchmarkDotNet.Utils
{
    public static class VectorUtils
    {
        private static readonly Func<Vector<float>, Vector<float>, Vector<float>> _vectorAdd = Vector.Add;
        private static readonly Func<Vector<float>, Vector<float>, Vector<float>> _vectorSubtract = Vector.Subtract;
        private static readonly Func<Vector<float>, Vector<float>, Vector<float>> _vectorMax = Vector.Max;
        private static readonly Func<Vector<float>, Vector<float>, Vector<float>> _vectorMin = Vector.Min;

        public static void Min(Span<float> lhs, Span<float> rhs, ref Span<float> result)
        {
            BinaryVectorOperation(lhs, rhs, _vectorMin, ref result);
        }

        public static void Max(Span<float> lhs, Span<float> rhs, ref Span<float> result)
        {
            BinaryVectorOperation(lhs, rhs, _vectorMax, ref result);
        }

        public static Span<float> Subtract(Span<float> lhs, Span<float> rhs)
        {
            Span<float> result = new float[128];
            BinaryVectorOperation(lhs, rhs, _vectorSubtract, ref result);

            return result;
        }

        public static Span<float> Add(Span<float> lhs, Span<float> rhs)
        {
            Span<float> result = new float[128];
            BinaryVectorOperation(lhs, rhs, _vectorAdd, ref result);

            return result;
        }

        public static Span<float> Multiply(Span<float> lhs, float value)
        {
            if (lhs.Length == 0)
                throw new ArgumentOutOfRangeException();

            if ((lhs.Length & (Vector<float>.Count - 1)) != 0)
                throw new ArgumentOutOfRangeException("Not a multiple of vector length");

            Span<float> result = new float[lhs.Length];
            ref var resultStart = ref result[0];

            for (var i = 0; i <= lhs.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                ref var part = ref Unsafe.As<float, Vector<float>>(ref Unsafe.Add(ref resultStart, i));
                part = Vector.Multiply(value, new Vector<float>(lhs.Slice(i, Vector<float>.Count)));
            }

            return result;
        }

        private static void BinaryVectorOperation(Span<float> lhs, Span<float> rhs,
            Func<Vector<float>, Vector<float>, Vector<float>> operation, ref Span<float> result)
        {
            if (lhs.Length > rhs.Length || lhs.Length == 0)
                throw new ArgumentOutOfRangeException();

            if ((lhs.Length & (Vector<float>.Count - 1)) != 0)
                throw new ArgumentOutOfRangeException("Not a multiple of vector length");

            ref var resultStart = ref result[0];

            for (var i = 0; i <= lhs.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                ref var part = ref Unsafe.As<float, Vector<float>>(ref Unsafe.Add(ref resultStart, i));

                part = operation(new Vector<float>(lhs.Slice(i, Vector<float>.Count)),
                    new Vector<float>(rhs.Slice(i, Vector<float>.Count)));
            }
        }
    }
}