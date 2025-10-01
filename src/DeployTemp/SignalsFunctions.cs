using System;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Collections.Generic;
using System.Linq;
using TimeZoneConverter;

namespace ExternalSignals.Api;

public static class SignalsFunctions
{
    static IReadOnlyList<double> GenerateDayAheadPrices(DateTime date)
    {
        int seed = int.Parse(date.ToString("yyyyMMdd"));
        var rand = new Random(seed);
        double Base(int h)
        {
            if (h < 6) return 0.65;
            if (h < 9) return 0.85;
            if (h < 15) return 1.05;
            if (h < 19) return 1.45;
            if (h < 22) return 1.10;
            return 0.75;
        }
        var values = new List<double>(24);
        for (int h = 0; h < 24; h++)
        {
            var jitter = (rand.NextDouble() - 0.5) * 0.16;
            var price = Math.Round(Base(h) + jitter, 3);
            price = Math.Clamp(price, 0.60, 1.60);
            values.Add(price);
        }
        return values;
    }

    static IReadOnlyList<double> GenerateHourlyTemps(DateTime date)
    {
        int doy = date.DayOfYear;
        double seasonal = 4 * Math.Cos((doy - 200) * Math.PI / 182.5);
        double Mean() => 16 + seasonal * 0.3;
        var rand = new Random(int.Parse(date.ToString("yyyyMMdd")) ^ 0x5A17);
        var values = new List<double>(24);
        for (int h = 0; h < 24; h++)
        {
            double angle = (h - 4) / 24.0 * 2 * Math.PI;
            double baseTemp = Mean() + 5 * Math.Sin(angle);
            double jitter = (rand.NextDouble() - 0.5) * 0.8;
            double temp = Math.Round(baseTemp + jitter, 2);
            values.Add(temp);
        }
        return values;
    }

    [Function("DayAheadPrice")]
    public static HttpResponseData GetDayAheadPrice([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "price/dayahead")] HttpRequestData req)
    {
        var cid = Guid.NewGuid().ToString("N");
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        string zone = query["zone"] ?? "SE3";
        string dateStr = query["date"];
        var tz = TZConvert.GetTimeZoneInfo("Europe/Stockholm");
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        DateTime date;
        if (string.IsNullOrWhiteSpace(dateStr))
        {
            date = nowLocal.Date;
        }
        else
        {
            Console.WriteLine($"[Func][{cid}] DayAheadPrice received raw date='{dateStr}'");
            if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                bad.Headers.Add("Content-Type", "application/json");
                bad.WriteString(JsonSerializer.Serialize(new { error = "Invalid date format. Use yyyy-MM-dd.", received = dateStr }));
                return bad;
            }
        }
        if (!string.Equals(zone, "SE3", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Func][{cid}] DayAheadPrice invalid zone={zone}");
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            bad.WriteString(JsonSerializer.Serialize(new { error = "Unsupported zone. Only SE3 supported in mock." }));
            return bad;
        }
        var values = GenerateDayAheadPrices(date);
        Console.WriteLine($"[Func][{cid}] DayAheadPrice zone=SE3 date={date:yyyy-MM-dd} values[0]={values[0]} values[23]={values[23]}");
        var payload = new { zone = "SE3", date = date.ToString("yyyy-MM-dd"), unit = "SEK/kWh", values };
        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/json");
        resp.WriteString(JsonSerializer.Serialize(payload));
        return resp;
    }

    [Function("WeatherHourly")]
    public static HttpResponseData GetWeatherHourly([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "weather/hourly")] HttpRequestData req)
    {
        var cid = Guid.NewGuid().ToString("N");
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        string city = query["city"] ?? "Stockholm";
        string dateStr = query["date"];
        var tz = TZConvert.GetTimeZoneInfo("Europe/Stockholm");
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        DateTime date;
        if (string.IsNullOrWhiteSpace(dateStr))
        {
            date = nowLocal.Date;
        }
        else
        {
            Console.WriteLine($"[Func][{cid}] WeatherHourly received raw date='{dateStr}'");
            if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                bad.Headers.Add("Content-Type", "application/json");
                bad.WriteString(JsonSerializer.Serialize(new { error = "Invalid date format. Use yyyy-MM-dd.", received = dateStr }));
                return bad;
            }
        }
        if (!string.Equals(city, "Stockholm", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Func][{cid}] WeatherHourly invalid city={city}");
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            bad.Headers.Add("Content-Type", "application/json");
            bad.WriteString(JsonSerializer.Serialize(new { error = "Unsupported city. Only Stockholm supported in mock." }));
            return bad;
        }
        var values = GenerateHourlyTemps(date);
        Console.WriteLine($"[Func][{cid}] WeatherHourly city=Stockholm date={date:yyyy-MM-dd} min={values.Min():F1} max={values.Max():F1}");
        var payload = new { city = "Stockholm", date = date.ToString("yyyy-MM-dd"), unit = "C", values };
        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/json");
        resp.WriteString(JsonSerializer.Serialize(payload));
        return resp;
    }

    [Function("Tariffs")]
    public static HttpResponseData GetTariffs([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tariffs")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        string country = query["country"] ?? "SE";
        string zone = query["zone"] ?? "SE3";
        var cid = Guid.NewGuid().ToString("N");
        Console.WriteLine($"[Func][{cid}] Tariffs country={country} zone={zone}");
        var payload = new { country, zone, tariff = "standard_office_electricity", fixed_annual_sek = 12000, variable_fee_sek_per_kwh = 0.12 };
        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/json");
        resp.WriteString(JsonSerializer.Serialize(payload));
        return resp;
    }

    [Function("Incentives")]
    public static HttpResponseData GetIncentives([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "incentives")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        string country = query["country"] ?? "SE";
        var cid = Guid.NewGuid().ToString("N");
        Console.WriteLine($"[Func][{cid}] Incentives country={country}");
        var payload = new { country, incentives = new[]{ new { name="GreenEnergyGrant", pct=0.15 }, new { name="HVACUpgradeSupport", pct=0.10 }, new { name="BatteryStorageOffset", pct=0.05 } } };
        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/json");
        resp.WriteString(JsonSerializer.Serialize(payload));
        return resp;
    }

    [Function("OpenApiSpec")]
    public static HttpResponseData GetOpenApi([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "openapi.json")] HttpRequestData req)
    {
        Console.WriteLine("[Func] OpenApiSpec served");
        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/json");
        var serverBase = $"{req.Url.Scheme}://{req.Url.Authority}/api";
        var root = new Dictionary<string, object?>
        {
            ["openapi"] = "3.0.1",
            ["info"] = new Dictionary<string, object?> { ["title"] = "External Signals API", ["version"] = "1.3.0" },
            ["servers"] = new object[]{ new Dictionary<string, object?> { ["url"] = serverBase } },
            ["paths"] = new Dictionary<string, object?>
            {
                ["/price/dayahead"] = new Dictionary<string, object?>
                {
                    ["get"] = new Dictionary<string, object?>
                    {
                        ["operationId"] = "DayAheadPrice",
                        ["summary"] = "Get day-ahead electricity prices (SEK/kWh) for 24 hours (zone=SE3 only)",
                        ["parameters"] = new object[]{
                            new Dictionary<string, object?> { ["name"]="zone", ["in"]="query", ["required"] = true, ["schema"] = new Dictionary<string, object?>{ ["type"]="string" } },
                            new Dictionary<string, object?> { ["name"]="date", ["in"]="query", ["required"] = false, ["schema"] = new Dictionary<string, object?>{ ["type"]="string", ["format"]="date" } }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["200"] = new Dictionary<string, object?>
                            {
                                ["description"] = "OK",
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["application/json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = new Dictionary<string, object?>{ ["$ref"] = "#/components/schemas/DayAheadPriceResponse" }
                                    }
                                }
                            }
                        }
                    }
                },
                ["/weather/hourly"] = new Dictionary<string, object?>
                {
                    ["get"] = new Dictionary<string, object?>
                    {
                        ["operationId"] = "WeatherHourly",
                        ["summary"] = "Get hourly temperature for 24 hours for a given city (Stockholm supported)",
                        ["parameters"] = new object[]{
                            new Dictionary<string, object?> { ["name"]="city", ["in"]="query", ["required"] = true, ["schema"] = new Dictionary<string, object?>{ ["type"]="string" } },
                            new Dictionary<string, object?> { ["name"]="date", ["in"]="query", ["required"] = false, ["schema"] = new Dictionary<string, object?>{ ["type"]="string", ["format"]="date" } }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["200"] = new Dictionary<string, object?>
                            {
                                ["description"] = "OK",
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["application/json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = new Dictionary<string, object?>{ ["$ref"] = "#/components/schemas/WeatherHourlyResponse" }
                                    }
                                }
                            }
                        }
                    }
                }
            },
            ["components"] = new Dictionary<string, object?>
            {
                ["schemas"] = new Dictionary<string, object?>
                {
                    ["DayAheadPriceResponse"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["zone"] = new Dictionary<string, object?>{ ["type"] = "string" },
                            ["date"] = new Dictionary<string, object?>{ ["type"] = "string", ["format"] = "date" },
                            ["unit"] = new Dictionary<string, object?>{ ["type"] = "string" },
                            ["values"] = new Dictionary<string, object?>{ ["type"] = "array", ["items"] = new Dictionary<string, object?>{ ["type"] = "number" }, ["minItems"] = 24, ["maxItems"] = 24 }
                        }
                    },
                    ["WeatherHourlyResponse"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["city"] = new Dictionary<string, object?>{ ["type"] = "string" },
                            ["date"] = new Dictionary<string, object?>{ ["type"] = "string", ["format"] = "date" },
                            ["unit"] = new Dictionary<string, object?>{ ["type"] = "string" },
                            ["values"] = new Dictionary<string, object?>{ ["type"] = "array", ["items"] = new Dictionary<string, object?>{ ["type"] = "number" }, ["minItems"] = 24, ["maxItems"] = 24 }
                        }
                    }
                }
            }
        };
        resp.WriteString(JsonSerializer.Serialize(root));
        return resp;
    }
}