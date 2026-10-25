namespace Nebula.Server.Models
{
    public sealed record PlatformEdition(
        string Sku,
        string Name,
        string Platform,
        string Blurb,
        long PriceCents,
        int Coins,
        bool FreeToPlay);

    public sealed record CheckoutRequest(string Sku);

    public static class PlatformCatalog
    {
        public static readonly PlatformEdition[] All =
        {
            new("play-free", "Browser Match", "Web",
                "The full competitive match, free in the browser. Same commander on every device.",
                0, 0, true),
            new("field-kit", "Field Kit", "Phone and tablet",
                "Install Nebula on your phone. Includes 1,400 syndicate coins for skins and the season.",
                699, 1400, false),
            new("commander", "Commander", "Windows and Mac",
                "Desktop install for a bigger board. Includes 2,800 syndicate coins.",
                1299, 2800, false),
            new("syndicate", "Syndicate", "Web, desktop, and mobile",
                "One purchase covers every install of this account, with 6,000 syndicate coins.",
                2499, 6000, false)
        };

        public static PlatformEdition? Paid(string? sku) =>
            All.FirstOrDefault(e => e.Sku == sku && !e.FreeToPlay);
    }
}
