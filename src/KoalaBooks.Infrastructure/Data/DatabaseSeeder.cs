using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Infrastructure.Data;

public static class DatabaseSeeder
{
    public static async Task SeedSruMappingsAsync(AppDbContext db)
    {
        if (await db.SruMappingRules.AnyAsync())
            return;

        db.SruMappingRules.AddRange(NeK1Rules());
        db.SruMappingRules.AddRange(Ink2Rules());
        await db.SaveChangesAsync();
    }

    // NE – Inkomst av näringsverksamhet (Enskild firma, Förenklat årsbokslut)
    // Source: NE_K1-201002.xlsx (BAS Förenklat Årsbokslut 2023)
    private static IEnumerable<SruMappingRule> NeK1Rules() =>
    [
        R(LegalForm.EnskildFirma, 7200, "B1",  "Immateriella anläggningstillgångar",                                          "1000"),
        R(LegalForm.EnskildFirma, 7210, "B2",  "Byggnader och markanläggningar",                                              "1110"),
        R(LegalForm.EnskildFirma, 7211, "B3",  "Mark och andra tillgångar som inte får skrivas av",                          "1130"),
        R(LegalForm.EnskildFirma, 7212, "B4",  "Maskiner och inventarier",                                                    "1220"),
        R(LegalForm.EnskildFirma, 7213, "B5",  "Övriga anläggningstillgångar",                                                "1300"),
        R(LegalForm.EnskildFirma, 7240, "B6",  "Varulager",                                                                   "1400"),
        R(LegalForm.EnskildFirma, 7250, "B7",  "Kundfordringar",                                                              "1500"),
        R(LegalForm.EnskildFirma, 7260, "B8",  "Övriga fordringar",                                                           "1600"),
        R(LegalForm.EnskildFirma, 7280, "B9",  "Kassa och bank",                                                              "1910"),
        R(LegalForm.EnskildFirma, 7300, "B10", "Eget kapital",                                                                "2010"),
        R(LegalForm.EnskildFirma, 7380, "B13", "Låneskulder",                                                                 "2330"),
        R(LegalForm.EnskildFirma, 7381, "B14", "Skatteskulder",                                                               "2610"),
        R(LegalForm.EnskildFirma, 7382, "B15", "Leverantörsskulder",                                                          "2440"),
        R(LegalForm.EnskildFirma, 7383, "B16", "Övriga skulder",                                                              "2900"),
        R(LegalForm.EnskildFirma, 7400, "R1",  "Försäljning och utfört arbete samt övriga momspliktiga intäkter",             "3000"),
        R(LegalForm.EnskildFirma, 7401, "R2",  "Momsfria intäkter",                                                          "3100"),
        R(LegalForm.EnskildFirma, 7402, "R3",  "Bil- och bostadsförmån m.m.",                                                 "3200"),
        R(LegalForm.EnskildFirma, 7403, "R4",  "Ränteintäkter m.m.",                                                         "8310"),
        R(LegalForm.EnskildFirma, 7500, "R5",  "Varor, material och tjänster",                                                "4000"),
        R(LegalForm.EnskildFirma, 7501, "R6",  "Övriga externa kostnader",                                                    "5000"),
        R(LegalForm.EnskildFirma, 7502, "R7",  "Anställd personal",                                                          "7000"),
        R(LegalForm.EnskildFirma, 7503, "R8",  "Räntekostnader m.m.",                                                        "8410"),
        R(LegalForm.EnskildFirma, 7504, "R9",  "Avskrivningar och nedskrivningar byggnader och markanläggningar",             "7700"),
        R(LegalForm.EnskildFirma, 7505, "R10", "Avskrivningar och nedskrivningar maskiner och inventarier",                   "7810"),
        R(LegalForm.EnskildFirma, 7440, "R11", "Bokfört resultat",                                                            "8990"),
    ];

    // Inkomstdeklaration 2 (Aktiebolag)
    // Source: INK2_P1_intervall-241119.xlsx (BAS 2023)
    private static IEnumerable<SruMappingRule> Ink2Rules() =>
    [
        // Balansräkning – Tillgångar
        R(LegalForm.Aktiebolag, 7201, "2.1",  "Koncessioner, patent, licenser, varumärken, hyresrätter, goodwill och liknande rättigheter", "1000-1087,1089-1099"),
        R(LegalForm.Aktiebolag, 7202, "2.2",  "Förskott avseende immateriella anläggningstillgångar",                                        "1088"),
        R(LegalForm.Aktiebolag, 7214, "2.3",  "Byggnader och mark",                                                                          "1100-1119,1130-1179,1190-1199"),
        R(LegalForm.Aktiebolag, 7215, "2.4",  "Maskiner, inventarier och övriga materiella anläggningstillgångar",                           "1200-1279,1290-1299"),
        R(LegalForm.Aktiebolag, 7216, "2.5",  "Förbättringsutgifter på annans fastighet",                                                    "112x"),
        R(LegalForm.Aktiebolag, 7217, "2.6",  "Pågående nyanläggningar och förskott avseende materiella anläggningstillgångar",               "118x,128x"),
        R(LegalForm.Aktiebolag, 7230, "2.7",  "Andelar i koncernföretag",                                                                    "131x"),
        R(LegalForm.Aktiebolag, 7231, "2.8",  "Andelar i intresseföretag och gemensamt styrda företag",                                      "1330-1335,1338-1339"),
        R(LegalForm.Aktiebolag, 7233, "2.9",  "Ägarintresse i övriga företag och andra långfristiga värdepappersinnehav",                    "135x,1336,1337"),
        R(LegalForm.Aktiebolag, 7232, "2.10", "Fordringar hos koncern-, intresse- och gemensamt styrda företag",                             "132x,1340-1345,1348-1349"),
        R(LegalForm.Aktiebolag, 7234, "2.11", "Lån till delägare eller närstående",                                                          "136x"),
        R(LegalForm.Aktiebolag, 7235, "2.12", "Fordringar hos övriga företag som det finns ett ägarintresse i och andra långfristiga fordringar", "137x,138x,1346,1347"),
        R(LegalForm.Aktiebolag, 7241, "2.13", "Råvaror och förnödenheter",                                                                   "141x,142x"),
        R(LegalForm.Aktiebolag, 7242, "2.14", "Varor under tillverkning",                                                                    "144x"),
        R(LegalForm.Aktiebolag, 7243, "2.15", "Färdiga varor och handelsvaror",                                                              "145x,146x"),
        R(LegalForm.Aktiebolag, 7244, "2.16", "Övriga lagertillgångar",                                                                      "149x"),
        R(LegalForm.Aktiebolag, 7245, "2.17", "Pågående arbeten för annans räkning",                                                         "147x"),
        R(LegalForm.Aktiebolag, 7246, "2.18", "Förskott till leverantörer",                                                                  "148x"),
        R(LegalForm.Aktiebolag, 7251, "2.19", "Kundfordringar",                                                                              "151x-155x,158x"),
        R(LegalForm.Aktiebolag, 7252, "2.20", "Fordringar hos koncern-, intresse- och gemensamt styrda företag",                             "156x,1570-1572,1574-1579,166x,1671-1672,1674-1679"),
        R(LegalForm.Aktiebolag, 7261, "2.21", "Fordringar hos övriga företag som det finns ett ägarintresse i",                              "161x,163x-165x,168x-169x,1573,1673"),
        R(LegalForm.Aktiebolag, 7262, "2.22", "Upparbetad men ej fakturerad intäkt",                                                         "162x"),
        R(LegalForm.Aktiebolag, 7263, "2.23", "Förutbetalda kostnader och upplupna intäkter",                                                "17xx"),
        R(LegalForm.Aktiebolag, 7270, "2.24", "Andelar i koncernföretag",                                                                    "186x"),
        R(LegalForm.Aktiebolag, 7271, "2.25", "Övriga kortfristiga placeringar",                                                             "1800-1859,1870-1899"),
        R(LegalForm.Aktiebolag, 7281, "2.26", "Kassa, bank och redovisningsmedel",                                                           "19xx"),

        // Balansräkning – Eget kapital och skulder
        R(LegalForm.Aktiebolag, 7301, "2.27", "Bundet eget kapital",                                                                         "208x"),
        R(LegalForm.Aktiebolag, 7302, "2.28", "Fritt eget kapital",                                                                          "209x"),
        R(LegalForm.Aktiebolag, 7321, "2.29", "Periodiseringsfonder",                                                                        "211x-213x"),
        R(LegalForm.Aktiebolag, 7322, "2.30", "Ackumulerade överavskrivningar",                                                              "215x"),
        R(LegalForm.Aktiebolag, 7323, "2.31", "Övriga obeskattade reserver",                                                                 "216x-219x"),
        R(LegalForm.Aktiebolag, 7331, "2.32", "Avsättningar för pensioner och liknande förpliktelser enligt tryggandelagen",                 "221x"),
        R(LegalForm.Aktiebolag, 7332, "2.33", "Övriga avsättningar för pensioner och liknande förpliktelser",                               "223x"),
        R(LegalForm.Aktiebolag, 7333, "2.34", "Övriga avsättningar",                                                                        "2220-2229,2240-2299"),
        R(LegalForm.Aktiebolag, 7350, "2.35", "Obligationslån",                                                                              "231x-232x"),
        R(LegalForm.Aktiebolag, 7351, "2.36", "Checkräkningskredit (långfristig)",                                                           "233x"),
        R(LegalForm.Aktiebolag, 7352, "2.37", "Övriga skulder till kreditinstitut (långfristig)",                                            "234x-235x"),
        R(LegalForm.Aktiebolag, 7353, "2.38", "Skulder till koncern-, intresse- och gemensamt styrda företag (långfristig)",                 "2360-2372,2374-2379"),
        R(LegalForm.Aktiebolag, 7354, "2.39", "Skulder till övriga företag som det finns ett ägarintresse i (långfristig)",                  "238x-239x,2373"),
        R(LegalForm.Aktiebolag, 7360, "2.40", "Checkräkningskredit (kortfristig)",                                                           "248x"),
        R(LegalForm.Aktiebolag, 7361, "2.41", "Övriga skulder till kreditinstitut (kortfristig)",                                            "241x"),
        R(LegalForm.Aktiebolag, 7362, "2.42", "Förskott från kunder",                                                                        "242x"),
        R(LegalForm.Aktiebolag, 7363, "2.43", "Pågående arbeten för annans räkning",                                                         "243x"),
        R(LegalForm.Aktiebolag, 7364, "2.44", "Fakturerad men ej upparbetad intäkt",                                                         "245x"),
        R(LegalForm.Aktiebolag, 7365, "2.45", "Leverantörsskulder",                                                                          "244x"),
        R(LegalForm.Aktiebolag, 7366, "2.46", "Växelskulder",                                                                                "2492"),
        R(LegalForm.Aktiebolag, 7367, "2.47", "Skulder till koncern-, intresse- och gemensamt styrda företag (kortfristig)",                 "2460-2472,2474-2479,2874-2879"),
        R(LegalForm.Aktiebolag, 7369, "2.48", "Skulder till övriga företag som det finns ett ägarintresse i (kortfristig)",                  "2490-2491,2493-2499,2600-2859,2880-2899"),
        R(LegalForm.Aktiebolag, 7368, "2.49", "Skatteskulder",                                                                               "25xx"),
        R(LegalForm.Aktiebolag, 7370, "2.50", "Upplupna kostnader och förutbetalda intäkter",                                                "29xx"),

        // Resultaträkning
        R(LegalForm.Aktiebolag, 7410, "3.1",  "Nettoomsättning",                                                                             "30xx-37xx"),
        R(LegalForm.Aktiebolag, 7411, "3.2",  "Förändring av lager av produkter i arbete, färdiga varor och pågående arbeten (netto +)",     "4900-4909,4930-4959,4970-4979,4990-4999", SruSignFilter.Positive),
        R(LegalForm.Aktiebolag, 7510, null,   "Förändring av lager av produkter i arbete, färdiga varor och pågående arbeten (netto -)",     "4900-4909,4930-4959,4970-4979,4990-4999", SruSignFilter.Negative),
        R(LegalForm.Aktiebolag, 7412, "3.3",  "Aktiverat arbete för egen räkning",                                                           "38xx"),
        R(LegalForm.Aktiebolag, 7413, "3.4",  "Övriga rörelseintäkter",                                                                      "39xx"),
        R(LegalForm.Aktiebolag, 7511, "3.5",  "Råvaror och förnödenheter",                                                                   "40xx-47xx,4910-4920"),
        R(LegalForm.Aktiebolag, 7512, "3.6",  "Handelsvaror",                                                                                "40xx-47xx,496x,498x"),
        R(LegalForm.Aktiebolag, 7513, "3.7",  "Övriga externa kostnader",                                                                    "50xx-69xx"),
        R(LegalForm.Aktiebolag, 7514, "3.8",  "Personalkostnader",                                                                           "70xx-76xx"),
        R(LegalForm.Aktiebolag, 7515, "3.9",  "Av- och nedskrivningar av materiella och immateriella anläggningstillgångar",                 "7700-7739,7750-7789,7800-7899"),
        R(LegalForm.Aktiebolag, 7516, "3.10", "Nedskrivningar av omsättningstillgångar utöver normala nedskrivningar",                       "774x,779x"),
        R(LegalForm.Aktiebolag, 7517, "3.11", "Övriga rörelsekostnader",                                                                     "79xx"),
        R(LegalForm.Aktiebolag, 7414, "3.12", "Resultat från andelar i koncernföretag (netto +)",                                            "8000-8069,8090-8099",          SruSignFilter.Positive),
        R(LegalForm.Aktiebolag, 7518, null,   "Resultat från andelar i koncernföretag (netto -)",                                            "8000-8069,8090-8099",          SruSignFilter.Negative),
        R(LegalForm.Aktiebolag, 7415, "3.13", "Resultat från andelar i intresseföretag och gemensamt styrda företag (netto +)",              "8100-8112,8114-8117,8119-8122,8124-8132,8134-8169,8190-8199", SruSignFilter.Positive),
        R(LegalForm.Aktiebolag, 7519, null,   "Resultat från andelar i intresseföretag och gemensamt styrda företag (netto -)",              "8100-8112,8114-8117,8119-8122,8124-8132,8134-8169,8190-8199", SruSignFilter.Negative),
        R(LegalForm.Aktiebolag, 7423, "3.14", "Resultat från övriga företag som det finns ett ägarintresse i (netto +)",                     "8113,8118,8123,8133",          SruSignFilter.Positive),
        R(LegalForm.Aktiebolag, 7530, null,   "Resultat från övriga företag som det finns ett ägarintresse i (netto -)",                     "8113,8118,8123,8133",          SruSignFilter.Negative),
        R(LegalForm.Aktiebolag, 7416, "3.15", "Resultat från övriga anläggningstillgångar (netto +)",                                        "8200-8269,8290-8299",          SruSignFilter.Positive),
        R(LegalForm.Aktiebolag, 7520, null,   "Resultat från övriga anläggningstillgångar (netto -)",                                        "8200-8269,8290-8299",          SruSignFilter.Negative),
        R(LegalForm.Aktiebolag, 7417, "3.16", "Övriga ränteintäkter och liknande resultatposter",                                            "8300-8369,8390-8399"),
        R(LegalForm.Aktiebolag, 7521, "3.17", "Nedskrivningar av finansiella anläggningstillgångar och kortfristiga placeringar",            "807x,808x,817x,818x,827x,828x,837x,838x"),
        R(LegalForm.Aktiebolag, 7522, "3.18", "Räntekostnader och liknande resultatposter",                                                  "84xx"),
        R(LegalForm.Aktiebolag, 7524, "3.19", "Lämnade koncernbidrag",                                                                       "883x"),
        R(LegalForm.Aktiebolag, 7419, "3.20", "Mottagna koncernbidrag",                                                                      "882x"),
        R(LegalForm.Aktiebolag, 7420, "3.21", "Återföring av periodiseringsfond",                                                            "8810,8819"),
        R(LegalForm.Aktiebolag, 7525, "3.22", "Avsättning till periodiseringsfond",                                                          "8810,8811"),
        R(LegalForm.Aktiebolag, 7421, "3.23", "Förändring av överavskrivningar (netto +)",                                                   "885x",                         SruSignFilter.Positive),
        R(LegalForm.Aktiebolag, 7526, null,   "Förändring av överavskrivningar (netto -)",                                                   "885x",                         SruSignFilter.Negative),
        R(LegalForm.Aktiebolag, 7422, "3.24", "Övriga bokslutsdispositioner (netto +)",                                                      "886x-889x",                    SruSignFilter.Positive),
        R(LegalForm.Aktiebolag, 7527, null,   "Övriga bokslutsdispositioner (netto -)",                                                      "886x-889x,884x",               SruSignFilter.Negative),
        R(LegalForm.Aktiebolag, 7528, "3.25", "Skatt på årets resultat",                                                                     "8900-8989"),
        R(LegalForm.Aktiebolag, 7450, "3.26", "Årets resultat, vinst",                                                                       "899x",                         SruSignFilter.Positive),
        R(LegalForm.Aktiebolag, 7550, "3.27", "Årets resultat, förlust",                                                                     "899x",                         SruSignFilter.Negative),
    ];

    private static SruMappingRule R(
        LegalForm legalForm,
        int sruCode,
        string? radLabel,
        string description,
        string accountPatterns,
        SruSignFilter sign = SruSignFilter.Any) => new()
    {
        LegalForm = legalForm,
        SruCode = sruCode,
        RadLabel = radLabel,
        Description = description,
        AccountPatterns = accountPatterns,
        Sign = sign,
    };
}
