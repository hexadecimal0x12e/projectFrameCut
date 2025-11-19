using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


public static class SimpleLocalizer
{
    /// <summary>
    /// Initialize the locater
    /// </summary>
    /// <param name="locateCode">The locate Name seted for this locate. Optional, default to the <code>System.Globalization.CultureInfo.CurrentUICulture.Name</code></param>
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
            IsFallbackMatched = true;
        }

        return localizer;

    }

    public static bool IsFallbackMatched = false;

    public static ISimpleLocalizerBase GetSpecificLocalizer(string locateCode)
    {
        if (!ISimpleLocalizerBase.GetMapping().TryGetValue(locateCode, out var localizer))
        {
            if (localizer is null) throw new KeyNotFoundException($"Can't find localizer '{locateCode}'.");
        }
        return localizer;
    }

    /// <summary>
    /// Get the result by reflection. This is slower than DynamicLookup and DynamicLookupWithArgs and may not work in some AOT scenarios.
    /// </summary>
    /// <param name="id">Localized string's ID</param>
    /// <param name="args">Arguments (option)</param>
    /// <returns>The result string</returns>
    /// <exception cref="KeyNotFoundException">key not found</exception>
    public static string DynamicLookupWithReflection(this ISimpleLocalizerBase localizerBase, string id, params object[]? args)
    {
        var method = localizerBase.GetType().GetMethod(id);
        if (method == null)
        {
            var prop = localizerBase.GetType().GetProperty(id);
            if (prop != null)
            {
                return (string)prop.GetValue(localizerBase)!;
            }
            throw new KeyNotFoundException($"Can't find the localized string for id '{id}'");
        }
        return (string)method.Invoke(localizerBase, args)!;
    }
}

