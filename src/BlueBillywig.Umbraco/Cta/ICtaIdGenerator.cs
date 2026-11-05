using System.Security.Cryptography;

namespace BlueBillywig.Umbraco.Cta;

/// <summary>Source of widget, track, in/out point, trigger and action ids. A seam so tests can seed it.</summary>
public interface ICtaIdGenerator
{
    /// <summary>A new id that starts with <paramref name="prefix"/> (w, t, i, c, a).</summary>
    string Next(string prefix);
}

/// <summary>The plugin's <c>prefix . bin2hex(random_bytes(8))</c>.</summary>
public sealed class RandomCtaIdGenerator : ICtaIdGenerator
{
    public string Next(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}
