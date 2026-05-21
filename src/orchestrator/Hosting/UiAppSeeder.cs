using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Hosting;

public static class UiAppSeeder {
    private static readonly string[] DefaultApps =
    {
        "console-admin",
        "continuo-ops-ui",
        "tech-shell-ui",
        "maestro-console",
        "qrmenu-web",
        "qrmenu-mobile",
        "kiosk-ui",
        "pos-offline",
        "public-web",
        "tcc-ui",
        "tcc-ops-ui",
        "tablet-ui",
        "continuo-web"
    };

    public static async Task SeedAsync(OrchestratorDbContext db) {
        // Incremental seed — early return KALDIRILDI (2026-05-20). Eski versiyon
        // "if (db.UiApps.Any()) return;" ile sadece boş DB'de seed yapıyordu,
        // mevcut DB'ye yeni eklenen app'ler (tech-shell-ui gibi) hiçbir zaman
        // düşmüyordu → orchestrator browser request'i için UiAppRegistry.Resolve(name)
        // null dönüyor → "unknown-ui" 403. Şimdi her startup'ta missing entries
        // append ediliyor; idempotent + foreign-key conflict yok (Name unique).
        var changed = false;
        foreach (var name in DefaultApps) {
            if (!db.UiApps.Any(x => x.Name == name)) {
                db.UiApps.Add(new UiApp { Name = name });
                changed = true;
            }
        }

        if (changed) {
            await db.SaveChangesAsync();
        }
    }
}
