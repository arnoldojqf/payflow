using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PayFlow.Contracts.Payments;
using PayFlow.PaymentApi.Outbox;

namespace PayFlow.PaymentApi.IntegrationTests;

/// <summary>
/// Publishes through the real Service Bus SDK and reads the message back from
/// the payment-processor subscription.
/// </summary>
/// <remarks>
/// The loop tests use a fake publisher, which proves nothing about the
/// transport: topic routing, the Subject the subscription filters on and the
/// MessageId duplicate detection relies on are all broker behaviour. This test
/// is the one that would catch a message that never arrives, or arrives
/// unrecognisable.
/// </remarks>
// Needs a real Service Bus namespace, so CI leaves it out with
// --filter-not-trait "Category=ServiceBus".
[Trait("Category", "ServiceBus")]
[Collection(DatabaseCollection.Name)]
public sealed class ServiceBusOutboxPublisherTests(PaymentApiFactory factory)
{
    private const string SubscriptionName = "payment-processor";
    private const string ConnectionStringKey = "ServiceBus:ConnectionString";

    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Published_message_arrives_on_the_payment_processor_subscription()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var options = ServiceBusOptions();

        await using var client = new ServiceBusClient(options.Value.ConnectionString);
        await using var publisher = new ServiceBusOutboxPublisher(client, options);

        // A receiver opened before publishing, so the message cannot arrive and
        // be waited for afterwards.
        await using var receiver = client.CreateReceiver(
            options.Value.TopicName,
            SubscriptionName,
            new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

        var paymentId = Guid.CreateVersion7();
        var occurredAt = DateTimeOffset.UtcNow;

        var message = new PendingOutboxMessage(
            Guid.CreateVersion7(),
            nameof(PaymentCreated),
            JsonSerializer.Serialize(
                new PaymentCreated(paymentId, 74.50m, "EUR", occurredAt),
                PayloadSerializerOptions),
            occurredAt,
            Attempts: 0);

        await publisher.PublishAsync(message, cancellationToken);

        var received = await ReceiveAsync(receiver, message.Id.ToString(), cancellationToken);

        Assert.Equal(nameof(PaymentCreated), received.Subject);
        Assert.Equal(ServiceBusOutboxPublisher.PayloadContentType, received.ContentType);

        var published = JsonSerializer.Deserialize<PaymentCreated>(
            received.Body.ToString(),
            PayloadSerializerOptions);
        var contract = Assert.IsType<PaymentCreated>(published);

        Assert.Equal(paymentId, contract.PaymentId);
        Assert.Equal(74.50m, contract.Amount);
        Assert.Equal("EUR", contract.Currency);

        // Completing it is the cleanup: the subscription is shared, so a message
        // left behind would be delivered to whatever runs next.
        await receiver.CompleteMessageAsync(received, cancellationToken);
    }

    /// <summary>
    /// Waits for one specific message, leaving anything else on the
    /// subscription for its real consumer.
    /// </summary>
    private static async Task<ServiceBusReceivedMessage> ReceiveAsync(
        ServiceBusReceiver receiver,
        string messageId,
        CancellationToken cancellationToken)
    {
        // Generous, because this is a real network round trip to Azure rather
        // than anything paced by the test.
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            var batch = await receiver.ReceiveMessagesAsync(
                maxMessages: 10,
                maxWaitTime: TimeSpan.FromSeconds(5),
                cancellationToken);

            foreach (var candidate in batch)
            {
                if (candidate.MessageId == messageId)
                {
                    return candidate;
                }

                // Someone else's message: hand it straight back rather than
                // holding its lock until it expires.
                await receiver.AbandonMessageAsync(candidate, cancellationToken: cancellationToken);
            }
        }

        Assert.Fail(
            $"No message with id {messageId} arrived on the {SubscriptionName} subscription "
            + "within the timeout.");

        return null!;
    }

    /// <summary>
    /// Topic name from the API's own configuration; the credential from the
    /// API's user-secrets, read explicitly so it does not depend on how the test
    /// host resolves the application name.
    /// </summary>
    private IOptions<ServiceBusOptions> ServiceBusOptions()
    {
        var hostConfiguration = factory.Services.GetRequiredService<IConfiguration>();

        var secrets = new ConfigurationBuilder()
            .AddUserSecrets(typeof(Program).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = hostConfiguration[ConnectionStringKey]
            ?? secrets[ConnectionStringKey];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Fail(
                $"No Service Bus connection string. Set it with: dotnet user-secrets set "
                + $"\"{ConnectionStringKey}\" \"<connection string>\" --project src/PayFlow.PaymentApi");
        }

        return Options.Create(new ServiceBusOptions
        {
            ConnectionString = connectionString,
            TopicName = hostConfiguration[$"{PaymentApi.Outbox.ServiceBusOptions.SectionName}:TopicName"]
                ?? throw new InvalidOperationException("No Service Bus topic name is configured."),
        });
    }
}
