using System;
using System.Globalization;
using UnityEngine;

namespace AirTools.Core
{
    /// Project logging. Acceptance checks use one grep-able line each (SPEC §7):
    /// <c>[AirTools.Check] M2.tape.window value=1.5003 expected=1.500 tol=0.010 PASS</c>
    public static class Log
    {
        public static void Info(string message) => Debug.Log($"[AirTools] {message}");
        public static void Warn(string message) => Debug.LogWarning($"[AirTools] {message}");
        public static void Error(string message) => Debug.LogError($"[AirTools] {message}");

        public static bool Check(string id, double value, double expected, double tolerance)
        {
            bool pass = Math.Abs(value - expected) <= tolerance;
            Debug.Log(FormatCheck(id, value, expected, tolerance, pass));
            return pass;
        }

        public static bool Check(string id, bool pass, string detail)
        {
            Debug.Log($"[AirTools.Check] {id} {detail} {(pass ? "PASS" : "FAIL")}");
            return pass;
        }

        public static string FormatCheck(string id, double value, double expected, double tolerance, bool pass)
        {
            var c = CultureInfo.InvariantCulture;
            return $"[AirTools.Check] {id} value={value.ToString("0.0000", c)} expected={expected.ToString("0.000", c)} " +
                   $"tol={tolerance.ToString("0.000", c)} {(pass ? "PASS" : "FAIL")}";
        }
    }
}
