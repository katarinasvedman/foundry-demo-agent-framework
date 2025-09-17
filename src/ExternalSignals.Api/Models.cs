namespace ExternalSignals.Api.Models
{
    public record DayAheadPriceResponse(string zone, string date, string unit, double[] values);
    public record WeatherHourlyResponse(string city, string date, string unit, double[] values);
}