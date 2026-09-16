using System.Data;
using Dapper;
using NpgsqlTypes;

namespace PayFlow.PaymentApi.Persistence;

/// <summary>
/// Type mappings the Dapper read path needs, registered once per process.
/// </summary>
public static class DapperMappings
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
            _registered = true;
        }
    }

    /// <summary>
    /// Maps timestamptz to <see cref="DateTimeOffset"/> in both directions.
    /// </summary>
    /// <remarks>
    /// Postgres stores timestamptz as an instant in UTC and keeps no offset, so
    /// Npgsql reads it back as a <see cref="DateTime"/>. Without this, Dapper
    /// sees no way to turn that into the DateTimeOffset the domain uses and
    /// refuses to materialise the row. EF applies the same conversion on the
    /// write path; this gives the read path the matching half.
    /// </remarks>
    private sealed class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset offset => offset,
            // Kind is Utc for timestamptz, which makes this a zero offset rather
            // than an assumption about the machine's local time zone.
            DateTime dateTime => new DateTimeOffset(
                DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new DataException(
                $"Cannot convert {value?.GetType().Name ?? "null"} to DateTimeOffset."),
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            if (parameter is Npgsql.NpgsqlParameter npgsqlParameter)
            {
                npgsqlParameter.NpgsqlDbType = NpgsqlDbType.TimestampTz;
            }

            parameter.Value = value.ToUniversalTime();
        }
    }
}
