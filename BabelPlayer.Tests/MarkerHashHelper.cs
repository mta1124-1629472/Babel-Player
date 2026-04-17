using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BabelPlayer.Tests;

public static class MarkerHashHelper
{
    public static string ComputeMarkerHash(string requirementsPath, string constraintsPath)
    {
        var content = $"python:3.11.6\n[requirements]\n{File.ReadAllText(requirementsPath)}\n[constraints]\n{File.ReadAllText(constraintsPath)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}
