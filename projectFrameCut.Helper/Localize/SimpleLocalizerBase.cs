
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


public static class SimpleLocalizer
{
    /// <summary>
    /// Initialize the Localizer
    /// </summary>
    /// <param name="locateCode">The locate Name set for this locate. Optional, default to the <code>System.Globalization.CultureInfo.CurrentUICulture.Name</code></param>
    /// <exception cref="InvalidOperationException">no any locate available</exception>
    public static ISimpleLocalizerBase Init(string? locateCode = null)
    { 
        if (locateCode == null)
        {
            locateCode = System.Globalization.CultureInfo.CurrentUICulture.Name;
        }
        if (!ISimpleLocalizerBase.GetMapping().TryGetValue(locateCode, out var localizer))
        {
            localizer = ISimpleLocalizerBase.GetMapping().First().Value;
            if(localizer is null) throw new InvalidOperationException("Can't find any localizer. Make sure you initialized the project correctly.");
            else IsFallbackMatchedLocalization = true;
        }

        return localizer;

    }

    /// <summary>
    /// Get whether the Localizer is inited in a fallback locate
    /// </summary>
    /// <remarks>
    /// This property is true when the locate code provided to <see cref="Init(string?)"/> is not found,
    /// You may show a prompt to user or log it for debug purpose if this is true.
    /// </remarks>
    public static bool IsFallbackMatchedLocalization { get; private set; } = false;


    /// <summary>
    /// Get or init the Localizer with a specific locate.
    /// </summary>
    /// <param name="locateCode">The locate Name set for this locate.</code></param>
    /// <exception cref="InvalidOperationException">no any locate available</exception>
    public static ISimpleLocalizerBase GetSpecificLocalizer(string locateCode)
    {
        if (!ISimpleLocalizerBase.GetMapping().TryGetValue(locateCode, out var localizer))
        {
            if (localizer is null) throw new KeyNotFoundException($"Can't find localizer '{locateCode}'.");
        }
        return localizer;
    }
}
