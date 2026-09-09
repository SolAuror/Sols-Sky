using System;
using UnityEngine;

namespace Sol.ToD
{
    /// <summary>Foreground moon coverage of the authored solar disc.</summary>
    public static class SolEclipseGeometry
    {
        const int IntegrationSteps = 128;
        static readonly Vector2[] IntegrationNodes = BuildIntegrationNodes();

        public static float ForegroundMoonDistance(float sunDistance, float moonDistance)
            => Mathf.Min(Mathf.Max(.01f, moonDistance), Mathf.Max(.01f, sunDistance) * .95f);

        public static float SolarOcclusion(Vector3 sunDirection, Vector3 moonDirection,
            float sunDiameterDegrees, float moonDiameterDegrees,
            float refractionStrength = 0f, float flattenStrength = 0f)
        {
            if (sunDiameterDegrees <= 0f || moonDiameterDegrees <= 0f
                || sunDirection.sqrMagnitude < 1e-8f || moonDirection.sqrMagnitude < 1e-8f)
                return 0f;
            Vector3 sun = ApparentDirection(sunDirection.normalized, refractionStrength,
                flattenStrength, out float sunFlatten);
            Vector3 moon = ApparentDirection(moonDirection.normalized, refractionStrength,
                flattenStrength, out float moonFlatten);
            double sunRadius = Math.Min(30f, sunDiameterDegrees) * .5 * Mathf.Deg2Rad;
            double moonRadius = Math.Min(30f, moonDiameterDegrees) * .5 * Mathf.Deg2Rad;
            // Chord length preserves precision when the centres almost coincide.
            double alignment = Math.Clamp(1d - (sun - moon).sqrMagnitude * .5d, -1d, 1d);
            // Flattening only shrinks discs, so this rejects all non-overlaps.
            if (alignment <= Math.Cos(sunRadius + moonRadius)) return 0f;
            if (sunFlatten < 1e-6f && moonFlatten < 1e-6f)
                return CircleCoverage(sunRadius, moonRadius, Math.Acos(alignment));

            // At the horizon use the shader's actual projected ellipses. Integrate
            // their intersection in the sun's tangent plane; fixed cached nodes
            // avoid allocations and per-frame trigonometry inside the loop.
            BodyFrame(sun, out Vector3 sunRight, out Vector3 sunUp);
            BodyFrame(moon, out Vector3 moonRight, out Vector3 moonUp);
            double sinSun = Math.Sin(sunRadius), sinMoon = Math.Sin(moonRadius);
            double rX = Math.Tan(sunRadius);
            double rY = sinSun / Math.Sqrt((1d + sunFlatten) * (1d + sunFlatten) - sinSun * sinSun);
            double m = sinMoon * sinMoon;
            double stretch = (1d + moonFlatten) * (1d + moonFlatten);
            double a0 = Vector3.Dot(sun, moonRight), ax = Vector3.Dot(sunRight, moonRight), ay = Vector3.Dot(sunUp, moonRight);
            double b0 = Vector3.Dot(sun, moonUp), bx = Vector3.Dot(sunRight, moonUp), by = Vector3.Dot(sunUp, moonUp);
            double quadratic = ay * ay + stretch * by * by - m;
            double area = 0d;
            for (int i = 1; i < IntegrationSteps; i++)
            {
                double x = rX * IntegrationNodes[i].x;
                double cos = IntegrationNodes[i].y;
                double solarHalfHeight = rY * cos;
                double a = a0 + ax * x, b = b0 + bx * x;
                double linear = 2d * (a * ay + stretch * b * by);
                double constant = a * a + stretch * b * b - m * (1d + x * x);
                double discriminant = linear * linear - 4d * quadratic * constant;
                if (discriminant <= 0d) continue;
                double root = Math.Sqrt(discriminant);
                double low = (-linear - root) / (2d * quadratic);
                double high = (-linear + root) / (2d * quadratic);
                double length = Math.Max(0d, Math.Min(solarHalfHeight, high) - Math.Max(-solarHalfHeight, low));
                area += length * rX * cos * ((i & 1) == 0 ? 2d : 4d);
            }
            return Mathf.Clamp01((float)(area / (3d * IntegrationSteps * rX * rY)));
        }

        static float CircleCoverage(double sun, double moon, double distance)
        {
            if (distance >= sun + moon) return 0f;
            if (distance <= Math.Abs(moon - sun))
                return moon >= sun ? 1f : (float)(moon * moon / (sun * sun));
            double d2 = distance * distance, s2 = sun * sun, m2 = moon * moon;
            double solarSector = Math.Acos(Math.Clamp((d2 + s2 - m2) / (2d * distance * sun), -1d, 1d));
            double lunarSector = Math.Acos(Math.Clamp((d2 + m2 - s2) / (2d * distance * moon), -1d, 1d));
            double lens = Math.Sqrt(Math.Max(0d, (-distance + sun + moon) * (distance + sun - moon)
                * (distance - sun + moon) * (distance + sun + moon)));
            return Mathf.Clamp01((float)((s2 * solarSector + m2 * lunarSector - .5d * lens) / (Math.PI * s2)));
        }

        // Mirrors SolApparentBodyDirection in Sol_Skybox.shader.
        static Vector3 ApparentDirection(Vector3 direction, float refraction, float flattenStrength, out float flatten)
        {
            float altitude = Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
            flatten = flattenStrength * Mathf.Clamp01(1f - altitude / 6f);
            if (refraction <= 0f) return direction;
            float h = Mathf.Max(altitude, -1f);
            float lift = Mathf.Max(1f / Mathf.Tan((h + 7.31f / (h + 4.4f)) * Mathf.Deg2Rad), 0f)
                / 60f * Mathf.Deg2Rad * refraction;
            Vector3 vertical = Vector3.up - direction * direction.y;
            return vertical.sqrMagnitude < 1e-8f ? direction : (direction + vertical.normalized * lift).normalized;
        }

        static void BodyFrame(Vector3 direction, out Vector3 right, out Vector3 up)
        {
            right = Vector3.Cross(Mathf.Abs(direction.y) < .999f ? Vector3.up : Vector3.right, direction).normalized;
            up = Vector3.Cross(direction, right);
        }

        static Vector2[] BuildIntegrationNodes()
        {
            var nodes = new Vector2[IntegrationSteps + 1];
            for (int i = 0; i <= IntegrationSteps; i++)
            {
                double angle = -.5d * Math.PI + Math.PI * i / IntegrationSteps;
                nodes[i] = new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle));
            }
            return nodes;
        }
    }
}
