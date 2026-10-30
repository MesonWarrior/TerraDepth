using System;

namespace TerraDepth.World
{
	/// <summary>Seeded improved Perlin noise, output roughly in [-1, 1].</summary>
	public sealed class Perlin
	{
		private readonly int[] p = new int[512];

		public Perlin(int seed)
		{
			var rng = new Random(seed);
			int[] perm = new int[256];
			for (int i = 0; i < 256; i++)
				perm[i] = i;
			for (int i = 255; i > 0; i--) {
				int j = rng.Next(i + 1);
				(perm[i], perm[j]) = (perm[j], perm[i]);
			}
			for (int i = 0; i < 512; i++)
				p[i] = perm[i & 255];
		}

		private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
		private static double Lerp(double t, double a, double b) => a + t * (b - a);

		private static double Grad(int hash, double x, double y, double z)
		{
			int h = hash & 15;
			double u = h < 8 ? x : y;
			double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
			return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
		}

		public double Noise(double x, double y, double z)
		{
			int X = (int)Math.Floor(x) & 255, Y = (int)Math.Floor(y) & 255, Z = (int)Math.Floor(z) & 255;
			x -= Math.Floor(x);
			y -= Math.Floor(y);
			z -= Math.Floor(z);
			double u = Fade(x), v = Fade(y), w = Fade(z);
			int A = p[X] + Y, AA = p[A] + Z, AB = p[A + 1] + Z;
			int B = p[X + 1] + Y, BA = p[B] + Z, BB = p[B + 1] + Z;
			return Lerp(w,
				Lerp(v, Lerp(u, Grad(p[AA], x, y, z), Grad(p[BA], x - 1, y, z)),
					Lerp(u, Grad(p[AB], x, y - 1, z), Grad(p[BB], x - 1, y - 1, z))),
				Lerp(v, Lerp(u, Grad(p[AA + 1], x, y, z - 1), Grad(p[BA + 1], x - 1, y, z - 1)),
					Lerp(u, Grad(p[AB + 1], x, y - 1, z - 1), Grad(p[BB + 1], x - 1, y - 1, z - 1))));
		}

		public double Noise(double x, double y) => Noise(x, y, 0.5);

		public double Fbm(double x, double y, double z, int octaves)
		{
			double sum = 0, amp = 1, norm = 0;
			for (int i = 0; i < octaves; i++) {
				sum += Noise(x, y, z) * amp;
				norm += amp;
				amp *= 0.5;
				x *= 2; y *= 2; z *= 2;
			}
			return sum / norm;
		}
	}
}
