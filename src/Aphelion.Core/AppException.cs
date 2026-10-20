namespace Aphelion.Core;

/// <summary>
/// An expected, user-facing failure. <see cref="Code"/> is a stable key the UI translates;
/// <see cref="Exception.Message"/> is an English fallback for logs and untranslated codes.
/// </summary>
public sealed class AppException : Exception
{
    public string Code { get; }
    public IReadOnlyDictionary<string, string> Args { get; }

    public AppException(string code, string message, IReadOnlyDictionary<string, string>? args = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Args = args ?? new Dictionary<string, string>();
    }

    public static AppException Of(string code, string message, params (string Key, object? Value)[] args) =>
        new(code, message, args.ToDictionary(a => a.Key, a => a.Value?.ToString() ?? ""));
}
