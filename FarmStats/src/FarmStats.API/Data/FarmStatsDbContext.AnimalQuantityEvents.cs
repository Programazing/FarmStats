using FarmStats.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FarmStats.API.Data;

public sealed partial class FarmStatsDbContext
{
    private static void ConfigureAnimalQuantityEvents(
        ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnimalHistoryEntry>()
            .ToTable("AnimalHistory", table =>
                table.HasCheckConstraint(
                    "CK_AnimalHistory_QuantityEvent",
                    """
                    (
                        "EventType" <> 4
                        AND "QuantityEventType" IS NULL
                        AND "PreviousQuantity" IS NULL
                        AND "NewQuantity" IS NULL
                        AND "QuantityDelta" IS NULL
                    )
                    OR
                    (
                        "EventType" = 4
                        AND "QuantityEventType" IS NOT NULL
                        AND "PreviousQuantity" IS NOT NULL
                        AND "NewQuantity" IS NOT NULL
                        AND "QuantityDelta" IS NOT NULL
                        AND "EffectiveDate" IS NOT NULL
                        AND "PreviousQuantity" >= 0
                        AND "NewQuantity" >= 0
                        AND "QuantityDelta"::bigint =
                            "NewQuantity"::bigint - "PreviousQuantity"::bigint
                        AND
                        (
                            ("QuantityEventType" = 0 AND "QuantityDelta" > 0)
                            OR
                            ("QuantityEventType" IN (1, 2) AND "QuantityDelta" < 0)
                            OR
                            (
                                "QuantityEventType" = 3
                                AND "QuantityDelta" <> 0
                                AND "Notes" IS NOT NULL
                                AND length(btrim("Notes")) > 0
                            )
                        )
                    )
                    """));
    }
}