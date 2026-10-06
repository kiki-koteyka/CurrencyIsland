using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DynamicIsland;

public enum CentralBank
{
    Russia = 0,
    Turkey = 1
}

public interface IRatesSource
{
    CentralBank Bank { get; }
    string Name { get; }
    int BaseIndex { get; }
    string BaseSign { get; }
    Task<List<CbrHistoryPoint>> FetchHistoryAsync(int days);
    Task<List<(DateTime Date, double Rate)>> FetchDynamicRangeAsync(string charCode, DateTime from, DateTime to);
}

public sealed class CbrtRatesProvider : IRatesSource
{
    private const string TodayUrl = "https://www.tcmb.gov.tr/kurlar/today.xml";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string[] Codes = { "USD", "EUR", "CNY", "AED", "RUB" };

    private readonly object _gate = new();
    private readonly Dictionary<string, double[]?> _days = new();
    private bool _loaded;

    public CentralBank Bank => CentralBank.Turkey;
    public string Name => "CBRT";
    public int BaseIndex => 5;
    public string BaseSign => "₺";

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CurrencyIsland", "cbrt-cache.json");

    private static string Key(DateTime date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                var stored = JsonSerializer.Deserialize<Dictionary<string, double[]?>>(File.ReadAllText(CachePath));
                if (stored == null) return;
                foreach (var pair in stored) _days[pair.Key] = pair.Value;
            }
            catch
            {
            }
        }
    }

    private void Save()
    {
        try
        {
            Dictionary<string, double[]?> copy;
            lock (_gate) copy = new Dictionary<string, double[]?>(_days);
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(copy));
        }
        catch
        {
        }
    }

    private static double[]? Parse(XDocument doc)
    {
        var values = new double[Codes.Length];
        foreach (var currency in doc.Root?.Elements("Currency") ?? Enumerable.Empty<XElement>())
        {
            var index = Array.IndexOf(Codes, (string?)currency.Attribute("CurrencyCode"));
            if (index < 0) continue;
            var unit = double.TryParse((string?)currency.Element("Unit"), NumberStyles.Any, CultureInfo.InvariantCulture, out var u) && u > 0 ? u : 1;
            if (double.TryParse((string?)currency.Element("ForexBuying"), NumberStyles.Any, CultureInfo.InvariantCulture, out var buying) && buying > 0)
                values[index] = buying / unit;
        }
        return values.Take(3).All(v => v > 0) ? values : null;
    }

    private async Task<(DateTime Date, double[]? Values)?> FetchLatestAsync()
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(TodayUrl);
            var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var tarih = (string?)doc.Root?.Attribute("Tarih");
            var date = DateTime.TryParseExact(tarih, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today;
            var values = Parse(doc);
            if (values == null) return null;
            EnsureLoaded();
            lock (_gate) _days[Key(date)] = values;
            return (date, values);
        }
        catch
        {
            return null;
        }
    }

    private async Task<double[]?> FetchDayAsync(DateTime date)
    {
        EnsureLoaded();
        var key = Key(date);
        lock (_gate)
        {
            if (_days.TryGetValue(key, out var cached)) return cached;
        }

        try
        {
            var url = $"https://www.tcmb.gov.tr/kurlar/{date:yyyyMM}/{date:ddMMyyyy}.xml";
            using var response = await Http.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (date.Date < DateTime.Today.AddDays(-1)) lock (_gate) _days[key] = null;
                return null;
            }
            response.EnsureSuccessStatusCode();
            var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()));
            var values = Parse(doc);
            if (values != null) lock (_gate) _days[key] = values;
            return values;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(DateTime Date, double[] Values)?> FetchNearestAsync(DateTime date, int maxBack)
    {
        for (var back = 0; back <= maxBack; back++)
        {
            var day = date.AddDays(-back);
            var values = await FetchDayAsync(day);
            if (values != null) return (day, values);
        }
        return null;
    }

    private static CbrHistoryPoint ToPoint(DateTime date, double[] v) => new()
    {
        Date = date,
        UsdRub = v[0],
        EurRub = v[1],
        CnyRub = v[2],
        AedRub = v[3],
        RubRate = v[4],
        TryRate = 1
    };

    public async Task<List<CbrHistoryPoint>> FetchHistoryAsync(int days)
    {
        var points = new List<CbrHistoryPoint>();
        var latest = await FetchLatestAsync();
        if (latest is not { Values: not null } first) return points;

        points.Add(ToPoint(first.Date, first.Values));
        var cursor = first.Date.AddDays(-1);
        var guard = 0;
        while (points.Count < days && guard < days + 12)
        {
            guard++;
            var values = await FetchDayAsync(cursor);
            if (values != null) points.Add(ToPoint(cursor, values));
            cursor = cursor.AddDays(-1);
        }

        Save();
        points.Reverse();
        return points;
    }

    public async Task<List<(DateTime Date, double Rate)>> FetchDynamicRangeAsync(string charCode, DateTime from, DateTime to)
    {
        var index = Array.IndexOf(Codes, charCode);
        var result = new List<(DateTime, double)>();
        if (index < 0) return result;

        var weekly = (to - from).TotalDays > 60;
        var dates = new List<DateTime>();
        for (var day = to.Date; day >= from.Date; day = day.AddDays(weekly ? -7 : -1)) dates.Add(day);

        var gate = new SemaphoreSlim(6);
        var found = new List<(DateTime Date, double[] Values)>();
        var tasks = dates.Select(async date =>
        {
            await gate.WaitAsync();
            try
            {
                var hit = weekly ? await FetchNearestAsync(date, 4) : (await FetchDayAsync(date) is { } v ? (date, v) : ((DateTime, double[])?)null);
                if (hit is { } h) lock (found) found.Add((h.Item1, h.Item2));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
        Save();

        foreach (var entry in found.GroupBy(f => f.Date).Select(g => g.First()).OrderBy(f => f.Date))
            if (entry.Values[index] > 0) result.Add((entry.Date, entry.Values[index]));
        return result;
    }
}
