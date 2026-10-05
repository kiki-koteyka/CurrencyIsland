using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DynamicIsland;

public sealed class CbrRatesSample
{
    public bool Available;
    public double UsdRub;
    public double EurRub;
    public double CnyRub;
    public double AedRub;
    public double TryRub;
    public DateTime Date;

    // Populated separately (see CbrRatesProvider.FetchWithPreviousAsync) -
    // CBR's daily endpoint only ever returns one date's worth of rates per
    // call, so "yesterday" needs its own request.
    public bool PreviousAvailable;
    public double UsdRubPrevious;
    public double EurRubPrevious;
    public double CnyRubPrevious;
    public DateTime PreviousDate;
}

public sealed class CbrHistoryPoint
{
    public DateTime Date;
    public double UsdRub;
    public double EurRub;
    public double CnyRub;
    public double AedRub;
    public double TryRub;
}

public sealed class CbrRatesProvider
{
    private const string Endpoint = "http://www.cbr.ru/scripts/XML_daily.asp";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    static CbrRatesProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<CbrRatesSample> FetchAsync()
    {
        var sample = await FetchForAsync(null);

        // CBR only publishes a new rate on business days, and its "current"
        // rate is really "as of the last publication" - so the previous
        // business day is exactly one calendar day back except across a
        // weekend, when Monday's "today" is really Friday's rate and asking
        // for "yesterday" (Sunday) would just hand back that same rate,
        // making the comparison a no-op. Step back to Friday in that case.
        if (sample.Available)
        {
            var previousDay = sample.Date.DayOfWeek switch
            {
                DayOfWeek.Monday => sample.Date.AddDays(-3),
                DayOfWeek.Sunday => sample.Date.AddDays(-2),
                _ => sample.Date.AddDays(-1)
            };
            var previous = await FetchForAsync(previousDay);
            if (previous.Available)
            {
                sample.PreviousAvailable = true;
                sample.UsdRubPrevious = previous.UsdRub;
                sample.EurRubPrevious = previous.EurRub;
                sample.CnyRubPrevious = previous.CnyRub;
                sample.PreviousDate = previous.Date;
            }
        }

        return sample;
    }

    // Walks backward day by day requesting date_req, collecting distinct
    // published dates until it has `days` of them - CBR only republishes on
    // business days, so a weekend request just echoes Friday's rate back,
    // which this dedupes by the ACTUAL returned Date, not the requested one.
    public async Task<List<CbrHistoryPoint>> FetchHistoryAsync(int days)
    {
        var points = new List<CbrHistoryPoint>();
        var latest = await FetchForAsync(null);
        if (!latest.Available) return points;

        points.Add(ToPoint(latest));
        var cursor = latest.Date.AddDays(-1);
        var guard = 0;
        while (points.Count < days && guard < days + 10)
        {
            guard++;
            var sample = await FetchForAsync(cursor);
            cursor = cursor.AddDays(-1);
            if (!sample.Available) continue;
            if (points.Any(p => p.Date == sample.Date)) continue;
            points.Add(ToPoint(sample));
        }

        points.Reverse();
        return points;
    }

    private static CbrHistoryPoint ToPoint(CbrRatesSample s) => new()
    {
        Date = s.Date,
        UsdRub = s.UsdRub,
        EurRub = s.EurRub,
        CnyRub = s.CnyRub,
        AedRub = s.AedRub,
        TryRub = s.TryRub
    };

    // CBR's internal per-currency codes (NOT the CharCode - these are only
    // used by the dynamic/range endpoint): USD=R01235, EUR=R01239,
    // CNY=R01375, AED=R01230.
    private static readonly Dictionary<string, string> DynamicValCodes = new()
    {
        ["USD"] = "R01235",
        ["EUR"] = "R01239",
        ["CNY"] = "R01375",
        ["AED"] = "R01230",
        ["TRY"] = "R01700J"
    };

    // XML_dynamic.asp returns every published rate for ONE currency across
    // an arbitrary date range in a single request - the right tool for a
    // month/year chart (FetchHistoryAsync would need hundreds of requests).
    public async Task<List<(DateTime Date, double Rate)>> FetchDynamicRangeAsync(string charCode, DateTime from, DateTime to)
    {
        var points = new List<(DateTime, double)>();
        if (!DynamicValCodes.TryGetValue(charCode, out var valCode)) return points;

        try
        {
            var url = $"http://www.cbr.ru/scripts/XML_dynamic.asp?date_req1={from:dd/MM/yyyy}&date_req2={to:dd/MM/yyyy}&VAL_NM_RQ={valCode}";
            var bytes = await Http.GetByteArrayAsync(url);
            var xml = Encoding.GetEncoding("windows-1251").GetString(bytes);
            var doc = XDocument.Parse(xml);

            foreach (var record in doc.Root?.Elements("Record") ?? Enumerable.Empty<XElement>())
            {
                var dateAttr = (string?)record.Attribute("Date");
                if (!DateTime.TryParseExact(dateAttr, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;

                var nominal = double.Parse((string?)record.Element("Nominal") ?? "1", CultureInfo.InvariantCulture);
                var valueRaw = ((string?)record.Element("Value") ?? "0").Replace(',', '.');
                if (!double.TryParse(valueRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) || nominal == 0)
                    continue;

                points.Add((date, value / nominal));
            }
        }
        catch
        {
            // best-effort - caller treats an empty list as "couldn't load"
        }

        return points;
    }

    private async Task<CbrRatesSample> FetchForAsync(DateTime? date)
    {
        try
        {
            var url = date is { } d0 ? $"{Endpoint}?date_req={d0:dd/MM/yyyy}" : Endpoint;
            var bytes = await Http.GetByteArrayAsync(url);
            var xml = Encoding.GetEncoding("windows-1251").GetString(bytes);
            var doc = XDocument.Parse(xml);

            var dateAttr = doc.Root?.Attribute("Date")?.Value;
            var date2 = DateTime.TryParseExact(dateAttr, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : DateTime.Today;

            return new CbrRatesSample
            {
                Available = true,
                UsdRub = ReadRate(doc, "USD"),
                EurRub = ReadRate(doc, "EUR"),
                CnyRub = ReadRate(doc, "CNY"),
                AedRub = ReadRate(doc, "AED"),
                TryRub = ReadRate(doc, "TRY"),
                Date = date2
            };
        }
        catch
        {
            return new CbrRatesSample { Available = false };
        }
    }

    private static double ReadRate(XDocument doc, string charCode)
    {
        var valute = doc.Root?.Elements("Valute")
            .FirstOrDefault(v => (string?)v.Element("CharCode") == charCode);
        if (valute == null) return 0;

        var nominal = double.Parse((string?)valute.Element("Nominal") ?? "1", CultureInfo.InvariantCulture);
        var value = double.Parse(((string?)valute.Element("Value") ?? "0").Replace(',', '.'), CultureInfo.InvariantCulture);
        return nominal == 0 ? 0 : value / nominal;
    }
}
