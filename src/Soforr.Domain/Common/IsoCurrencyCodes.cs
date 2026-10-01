namespace Soforr.Domain.Common;

/// <summary>
/// A maintained snapshot of the current ISO 4217 currency set used by Soforr, not a complete ISO 4217 table and not
/// derived from the host's culture data (which differs between operating systems and containers).
/// Intentionally excluded: fund codes (for example CHE, CLF, USN), precious metals (XAU, XAG, XPT, XPD), test and
/// "no currency" codes (XTS, XXX), special-purpose accounting units that are not a country's currency (XAD, XBA to XBD,
/// XDR, XSU, XUA), and withdrawn or historical currencies (for example HRK, ZWL, SLL, DEM).
/// Update the list when ISO 4217 changes.
/// </summary>
internal static class IsoCurrencyCodes
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "AED", "AFN", "ALL", "AMD", "AOA", "ARS", "AUD", "AWG", "AZN",
        "BAM", "BBD", "BDT", "BHD", "BIF", "BMD", "BND", "BOB", "BRL", "BSD", "BTN", "BWP", "BYN", "BZD",
        "CAD", "CDF", "CHF", "CLP", "CNY", "COP", "CRC", "CUP", "CVE", "CZK",
        "DJF", "DKK", "DOP", "DZD",
        "EGP", "ERN", "ETB", "EUR",
        "FJD", "FKP",
        "GBP", "GEL", "GHS", "GIP", "GMD", "GNF", "GTQ", "GYD",
        "HKD", "HNL", "HTG", "HUF",
        "IDR", "ILS", "INR", "IQD", "IRR", "ISK",
        "JMD", "JOD", "JPY",
        "KES", "KGS", "KHR", "KMF", "KPW", "KRW", "KWD", "KYD", "KZT",
        "LAK", "LBP", "LKR", "LRD", "LSL", "LYD",
        "MAD", "MDL", "MGA", "MKD", "MMK", "MNT", "MOP", "MRU", "MUR", "MVR", "MWK", "MXN", "MYR", "MZN",
        "NAD", "NGN", "NIO", "NOK", "NPR", "NZD",
        "OMR",
        "PAB", "PEN", "PGK", "PHP", "PKR", "PLN", "PYG",
        "QAR",
        "RON", "RSD", "RUB", "RWF",
        "SAR", "SBD", "SCR", "SDG", "SEK", "SGD", "SHP", "SLE", "SOS", "SRD", "SSP", "STN", "SVC", "SYP", "SZL",
        "THB", "TJS", "TMT", "TND", "TOP", "TRY", "TTD", "TWD", "TZS",
        "UAH", "UGX", "USD", "UYU", "UZS",
        "VED", "VES", "VND", "VUV",
        "WST",
        "XAF", "XCD", "XCG", "XOF", "XPF",
        "YER",
        "ZAR", "ZMW", "ZWG",
    };

    public static bool Contains(string code) => Codes.Contains(code);
}
