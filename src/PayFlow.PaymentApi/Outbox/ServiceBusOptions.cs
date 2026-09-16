using System.ComponentModel.DataAnnotations;

namespace PayFlow.PaymentApi.Outbox;

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// Namespace connection string. Kept out of appsettings.json: it is a
    /// credential, so it comes from user-secrets locally and from the
    /// environment elsewhere.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Topic every payment event is published to.</summary>
    [Required(AllowEmptyStrings = false)]
    public string TopicName { get; set; } = string.Empty;
}
