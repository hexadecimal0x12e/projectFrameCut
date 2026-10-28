using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace projectFrameCut.Render.Effect;

public sealed class AdvancedMathEffectProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public AdvancedMathEffectProvider(int inputCount)
    {
        if (inputCount is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(inputCount));
        InputCount = inputCount;
        Name = TypeName;
    }

    public int InputCount { get; }
    public override string TypeName => "AdvancedMath" + InputCount;
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override bool IsDeterministic => true;
    public override IReadOnlyList<Stage> SupportedStages => EffectStages.Values;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.ValueProvider;
    public string Operation => GetField("Operation")?.ToString() ?? AdvancedMathMethods.DefaultOperation(InputCount);
    public static object ParseNumber(object value) => AdvancedMathMethods.ReadNumber(value);
    Dictionary<string, string> IEffectProvider.ParametersType => Fields.Keys.ToDictionary(k => k,
        k => k is "A" or "B" or "C" ? "object" : "string");

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field("Operation", EffectArgumentFieldType.String | EffectArgumentFieldType.CannotBeDynamic,
            AdvancedMathMethods.DefaultOperation(InputCount), presetOptions: AdvancedMathMethods.Names(InputCount)),
    ];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        Enumerable.Range(0, InputCount).ToDictionary(i => "ABC"[i].ToString(),
            i => NamedField("ABC"[i].ToString(), InputName(i)));

    protected override EffectArgumentFieldDescriptor DefineOutField() => OutFields["Value"];

    private EffectArgumentFieldDescriptor NamedField(string id, string name) =>
        Field(id, EffectArgumentFieldType.Numeric, "0") with
        {
            DisplayName = name,
            LocalizedNameKey = "Effect_AdvancedMathField_" + name,
        };

    private string InputName(int index) => (Operation, index) switch
    {
        ("Sin" or "Cos" or "Tan" or "SinCos" or "ToDegree", 0) => "Radians",
        ("ToRadius", 0) => "Degrees",
        ("Asin", 0) => "Sine",
        ("Acos", 0) => "Cosine",
        ("Atan", 0) => "Tangent",
        ("Asinh", 0) => "HyperbolicSine",
        ("Acosh", 0) => "HyperbolicCosine",
        ("Atanh", 0) => "HyperbolicTangent",
        ("Sqrt" or "Cbrt" or "ReciprocalSqrtEstimate", 0) => "Radicand",
        ("Exp", 0) => "Exponent",
        ("Pow", 0) => "Base",
        ("Pow" or "ScaleB", 1) => "Exponent",
        ("Log", 1) => "Base",
        ("Round", 1) => "DecimalPlaces",
        ("Round", 2) => "RoundingMode",
        ("Atan2", 0) => "Y",
        ("Atan2", 1) => "X",
        ("CopySign", 0) => "Magnitude",
        ("CopySign", 1) => "SignSource",
        ("DivRem" or "IEEERemainder", 0) => "Dividend",
        ("DivRem" or "IEEERemainder", 1) => "Divisor",
        ("BigMul" or "FusedMultiplyAdd", 0) => "FirstFactor",
        ("BigMul" or "FusedMultiplyAdd", 1) => "SecondFactor",
        ("FusedMultiplyAdd", 2) => "Addend",
        ("Clamp", 1) => "Minimum",
        ("Clamp", 2) => "Maximum",
        ("Max" or "MaxMagnitude" or "Min" or "MinMagnitude", 0) => "FirstNumber",
        ("Max" or "MaxMagnitude" or "Min" or "MinMagnitude", 1) => "SecondNumber",
        _ => "Number",
    };

    private string OutputName => Operation switch
    {
        "Abs" => "AbsoluteValue",
        "Acos" or "Asin" or "Atan" or "Atan2" or "ToRadius" => "Radians",
        "Acosh" => "InverseHyperbolicCosine",
        "Asinh" => "InverseHyperbolicSine",
        "Atanh" => "InverseHyperbolicTangent",
        "BigMul" => "Product",
        "BitDecrement" => "PreviousNumber",
        "BitIncrement" => "NextNumber",
        "Cbrt" => "CubeRoot",
        "Ceiling" => "Ceiling",
        "Clamp" => "ClampedValue",
        "CopySign" => "SignedValue",
        "Cos" => "Cosine",
        "Cosh" => "HyperbolicCosine",
        "DivRem" => "Quotient",
        "Exp" => "Exponential",
        "Floor" => "Floor",
        "FusedMultiplyAdd" => "MultiplyAddResult",
        "IEEERemainder" => "Remainder",
        "ILogB" => "BinaryExponent",
        "Log" => InputCount == 1 ? "NaturalLogarithm" : "Logarithm",
        "Log10" => "CommonLogarithm",
        "Log2" => "BinaryLogarithm",
        "Max" => "Maximum",
        "MaxMagnitude" => "MaximumMagnitude",
        "Min" => "Minimum",
        "MinMagnitude" => "MinimumMagnitude",
        "Pow" => "Power",
        "ReciprocalEstimate" => "ReciprocalEstimate",
        "ReciprocalSqrtEstimate" => "ReciprocalSquareRootEstimate",
        "Round" => "RoundedValue",
        "ScaleB" => "ScaledValue",
        "Sign" => "Sign",
        "Sin" or "SinCos" => "Sine",
        "Sinh" => "HyperbolicSine",
        "Sqrt" => "SquareRoot",
        "Tan" => "Tangent",
        "Tanh" => "HyperbolicTangent",
        "ToDegree" => "Degrees",
        "Truncate" => "IntegerPart",
        _ => "Result",
    };

    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields
    {
        get
        {
            var fields = new Dictionary<string, EffectArgumentFieldDescriptor>
            {
                ["Value"] = NamedField("Value", OutputName),
            };
            if (Operation is "SinCos" or "DivRem")
            {
                string key = Operation == "SinCos" ? "Cos" : "Remainder";
                fields[key] = NamedField(key, Operation == "SinCos" ? "Cosine" : "Remainder");
            }
            return fields;
        }
    }
}

public sealed class AdvancedMathEffect(int inputCount) : IEffect, IMultipleOutputEffect
{
    public string TypeName => "AdvancedMath" + inputCount;
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public bool IsDeterministic => true;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Advanced Math";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new AdvancedMathEffect(inputCount) { Parameters = parameters };

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        string operation = context.Parameters.GetValueOrDefault("Operation")?.ToString()
            ?? AdvancedMathMethods.DefaultOperation(inputCount);
        var inputs = Enumerable.Range(0, inputCount)
            .Select(i => AdvancedMathMethods.ReadNumber(context.Parameters.GetValueOrDefault("ABC"[i].ToString())
                ?? throw new ArgumentException($"AdvancedMath requires input {"ABC"[i]}."))).ToArray();
        return AdvancedMathMethods.Compute(operation, inputs);
    }
}

internal static class AdvancedMathMethods
{
    private sealed class Overload(MethodInfo method)
    {
        public MethodInfo Method { get; } = method;
        public ParameterInfo[] Parameters { get; } = method.GetParameters();
        public MethodInvoker Invoker { get; } = MethodInvoker.Create(method);
    }

    private static readonly Dictionary<(string Name, int Count), Overload[]> Methods = CreateMethods();
    private static readonly ConcurrentDictionary<(string, Type, Type?, Type?), Overload[]> Ranked = new();
    private static readonly string[][] OperationNames = Enumerable.Range(1, 3)
        .Select(n => Methods.Keys.Where(k => k.Count == n).Select(k => k.Name)
            .Concat(n == 1 ? ["ToDegree", "ToRadius"] : Array.Empty<string>())
            .Order(StringComparer.Ordinal).ToArray()).ToArray();

    public static string DefaultOperation(int count) => count switch
    {
        1 => "Sin",
        2 => "Pow",
        3 => "Clamp",
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };

    public static string[] Names(int count) => OperationNames[count - 1];

    private static Dictionary<(string, int), Overload[]> CreateMethods()
    {
        var methods = typeof(Math).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            // BigMul returns the complete product through its non-out overloads.
            .Where(m => m.Name != "BigMul" || !m.GetParameters().Any(p => p.IsOut))
            .Select(m => new Overload(m))
            .GroupBy(m => (m.Method.Name, m.Parameters.Count(p => !p.IsOut)))
            .Where(g => g.Key.Item2 is >= 1 and <= 3)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Parameters.Length).ThenBy(m => m.Method.ToString(), StringComparer.Ordinal).ToArray());
        Log($"Advanced math: registered {methods.Count} method/input-count combinations.");
        return methods;
    }

    public static object ReadNumber(object value)
    {
        if (value is JsonElement json) value = json.ValueKind == JsonValueKind.Number ? json.GetRawText()
            : throw new ArgumentException("AdvancedMath requires numeric inputs.");
        if (value is string text)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
            if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong u)) return u;
            if (Int128.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i128)) return i128;
            if (UInt128.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u128)) return u128;
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        return value switch
        {
            Half h => (double)h,
            nint i => (long)i,
            nuint u => (ulong)u,
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal or Int128 or UInt128 => value,
            _ => throw new ArgumentException("AdvancedMath requires numeric inputs."),
        };
    }

    private static int Rank(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.SByte or TypeCode.Byte => 0,
        TypeCode.Int16 or TypeCode.UInt16 => 1,
        TypeCode.Int32 or TypeCode.UInt32 => 2,
        TypeCode.Int64 or TypeCode.UInt64 => 3,
        TypeCode.Single => 4,
        TypeCode.Double => 5,
        TypeCode.Decimal => 6,
        _ => 7,
    };

    private static int Cost(Type source, Type target)
    {
        if (source == target) return 0;
        if (target.IsEnum) return 50;
        if (target == typeof(nint)) target = typeof(long);
        if (target == typeof(nuint)) target = typeof(ulong);
        int a = Rank(source), b = Rank(target);
        if (a == 7 && (b is 4 or 5)) return 40;
        if ((a is 2 or 3) && target == typeof(float)) return 24;
        if (a == 3 && target == typeof(double)) return 20;
        bool unsignedTarget = target == typeof(byte) || target == typeof(ushort) || target == typeof(uint) || target == typeof(ulong);
        bool unsignedSource = source == typeof(byte) || source == typeof(ushort) || source == typeof(uint) || source == typeof(ulong);
        if (b < a || b <= 3 && unsignedTarget != unsignedSource && (unsignedTarget || b == a)
            || target == typeof(decimal) && (a is 4 or 5)) return 30 + Math.Abs(b - a);
        return 10 + b - a;
    }

    private static object ConvertNumber(object value, Type target)
    {
        if (value.GetType() == target) return value;
        if (target.IsEnum)
        {
            var mode = Enum.ToObject(target, ConvertNumber(value, typeof(int)));
            return Enum.IsDefined(target, mode) ? mode : throw new ArgumentException($"Invalid {target.Name} value: {value}.");
        }
        if (target == typeof(nint)) return checked((nint)(long)ConvertNumber(value, typeof(long)));
        if (target == typeof(nuint)) return checked((nuint)(ulong)ConvertNumber(value, typeof(ulong)));
        if (value is Int128 i) value = target == typeof(double) || target == typeof(float) ? (object)(double)i : checked((decimal)i);
        if (value is UInt128 u) value = target == typeof(double) || target == typeof(float) ? (object)(double)u : checked((decimal)u);
        if (Rank(target) <= 3)
        {
            decimal n = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (decimal.Truncate(n) != n) throw new ArgumentException("An integer argument cannot have a fractional part.");
        }
        return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    public static IReadOnlyDictionary<string, object?> Compute(string operation, object[] inputs)
    {
        if (inputs.Length == 1 && (operation is "ToDegree" or "ToRadius"))
            return new Dictionary<string, object?>
            {
                ["Value"] = (double)ConvertNumber(inputs[0], typeof(double))
                    * (operation == "ToDegree" ? 180d / Math.PI : Math.PI / 180d),
            };
        if (!Methods.TryGetValue((operation, inputs.Length), out var methods))
            throw new ArgumentException($"Unknown {inputs.Length}-input Math method '{operation}'.");
        var key = (operation, inputs[0].GetType(), inputs.Length > 1 ? inputs[1].GetType() : null,
            inputs.Length > 2 ? inputs[2].GetType() : null);
        var candidates = Ranked.GetOrAdd(key, _ => methods.OrderBy(m => m.Parameters.Where(p => !p.IsOut)
            .Select((p, i) => Cost(inputs[i].GetType(), p.ParameterType)).Sum()).ThenBy(m => m.Parameters.Length).ToArray());
        foreach (var method in candidates)
        {
            var args = new object?[method.Parameters.Length];
            try
            {
                int i = 0;
                for (int p = 0; p < args.Length; p++)
                    if (!method.Parameters[p].IsOut) args[p] = ConvertNumber(inputs[i++], method.Parameters[p].ParameterType);
            }
            catch (Exception ex) when (ex is OverflowException or FormatException or ArgumentException)
            {
                continue;
            }

            object? value = method.Invoker.Invoke(null, args.AsSpan());
            var result = new Dictionary<string, object?>();
            if (value is ITuple tuple)
            {
                result["Value"] = NormalizeOutput(tuple[0]);
                result[operation == "SinCos" ? "Cos" : "Remainder"] = NormalizeOutput(tuple[1]);
            }
            else
            {
                result["Value"] = NormalizeOutput(value);
                for (int p = 0; p < args.Length; p++)
                    if (method.Parameters[p].IsOut)
                        result["Remainder"] = NormalizeOutput(args[p]);
            }
            return result;
        }
        throw new ArgumentException($"Math.{operation} has no overload accepting the supplied numeric values.");
    }

    private static object? NormalizeOutput(object? value) => value switch
    {
        nint i => (long)i,
        nuint u => (ulong)u,
        _ => value,
    };
}
