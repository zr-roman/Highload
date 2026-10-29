using System.Diagnostics;

namespace Payments.Api.Telemetry;

/// <summary>The service's own spans. The name matches AddSource(serviceName) in Platform, otherwise they are not exported.</summary>
public static class PaymentsTelemetry
{
    public static readonly ActivitySource ActivitySource = new("payments-api");
}
