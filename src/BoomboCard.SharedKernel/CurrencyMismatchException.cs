namespace BoomboCard.SharedKernel;

/// <summary>
/// Thrown rather than returned as an Error: adding AED to USD is a programming mistake, not
/// a business outcome a caller could sensibly handle.
/// </summary>
public sealed class CurrencyMismatchException(string left, string right)
    : InvalidOperationException($"Cannot combine Money in '{left}' with Money in '{right}'.");
