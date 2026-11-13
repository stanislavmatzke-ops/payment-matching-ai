# Modernizace: Delphi + PL/SQL → .NET

> 🇬🇧 [English](../modernization.md) · 🇨🇿 Česky

Jak se fiktivní legacy modul FinPro PAYMATCH ([legacy/](../../legacy)) bezpečně převádí do .NET.
Postup: porozumět → zafixovat chování → přepsat → obrůst (strangler fig).

## 1. Porozumění (fáze 3, hotovo)

AI agent přečetl legacy kód a napsal [business-rules.md](../business-rules.md): 24 pravidel, každé s
odkazem na řádek legacy zdrojáku, 15 rozdílů mezi Delphi a PL/SQL, 13 rizik s konkrétními vstupy a 12
otevřených otázek pro byznys.

Aby to byl férový test, extrakce běžela v **samostatné session agenta bez jakéhokoli kontextu**. Smělá
číst jen `legacy/`, formát dat a konvence repa. Nespouštěla kód a neviděla golden master, tento
dokument ani seznam pravidel zasazených do fiktivního legacy kódu. Výsledek pak zkontroloval člověk a
hlavní agent:

- **Našla všech 13 zasazených pravidel**, včetně záměrné chyby a jejího dominového efektu.
- Agent našel i **skutečné problémy, které nikdo záměrně nezasadil**. Například PL/SQL verze uřízne
  `FAKTURA PRO FIRMU ABC 2026-1234` na fakturu `2026-1` a chybějící měna se v PL/SQL páruje jako CZK.
- 17 namátkově ověřených odkazů na řádky sedělo. Jeden příklad používal reálně znějící jméno; bylo
  nahrazeno fiktivním.
- **Jedno pravidlo bylo o jedničku vedle:** okno pro hledání ve zprávě. Dokument říkal „první číslice
  na pozici 0–14“, legacy kód přijme i pozici 15. Chytily to charakterizační testy v kroku 3 a potvrdil
  zkušební běh legacy binárky. Dokument byl opraven.

Ponaučení: AI byla důkladnější než autor fikce a její konkrétní příklady se rovnou proměnily v unit
testy. Přesnost na úrovni řádku ale pořád potřebuje kontrolu proti běžícímu kódu.

## 2. Charakterizace – golden master (fáze 2, hotovo)

Legacy modul se překládá Free Pascalem a běží jako CLI (ADR 0003). Proběhl nad dvěma vygenerovanými
sadami a jeho výstup je commitnutý jako golden master:

| Sada | Plateb | Výstup |
|---|---|---|
| `data/sample` (seed 42) | 300 | `legacy/golden/seed-42/legacy_matches.csv` |
| `legacy/golden/seed-2005/input` | 1 000 | `legacy/golden/seed-2005/legacy_matches.csv` |

CI legacy modul při každém pushi znovu přeloží a spadne, když se výstup změní.

### Výchozí stav legacy proti správným odpovědím

Změřeno jednou proti správným odpovědím generátoru (`expected.csv`, který legacy modul nikdy nečte).
„Automaticky použito“ znamená stav `P` nebo `C`.

| Metrika | Seed 42 (300) | Seed 2005 (1 000) |
|---|---|---|
| Automaticky použito | 197 (65,7 %) | 662 (66,2 %) |
| … správně a bezpečné automatizovat | 160 | 530 |
| … správná faktura, ale měl rozhodnout člověk | 34 | 113 |
| … **špatná faktura** | **3** | **19** |
| Precision automatických spárování | 81,2 % | 80,1 % |
| Spárování podle prompt injection | 0 | 0 |

Co čísla ukazují podle typu případu:

- **Přesný VS + částka** funguje (443 ze 450 ve větší sadě). Těch 7 chybějících je vedlejší účinek
  špatných spárování: dřívější platba se špatně spárovala se stejnou fakturou a správná platba je pak
  odmítnuta jako duplicita.
- **Špatné faktury** pocházejí všechny z překlepů ve VS. Některé se najdou hledáním podřetězce a
  spárují se s jinou fakturou jako částečná úhrada.
- **Částečné úhrady a platby od třetích stran** se párují automaticky, bez člověka.
- **Cizí měna, platby více faktur a překlepy ve VS** zůstávají většinou nespárované pro ruční práci.
- **Prompt injection** na legacy nefunguje, ale jen náhodou: pravidla buď neumí přečíst podvržené
  číslo faktury, nebo nesedí částka.

Tohle je laťka, kterou musí nový systém překonat: precision ≥ 99 % při vyšší automatizaci.

## 3. Port do .NET (fáze 3, hotovo)

Pravidla jsou v [`src/PaymentMatching.Rules`](../../src/PaymentMatching.Rules): malé třídy pojmenované
podle obchodních pravidel (`VariableSymbol`, `MessageVsParser`, `InvoiceLookup`, `PaymentMatcher`,
`LegacyCsv`), 62 unit testů podle příkladů v business-rules.md a 4 golden master testy.
`tools/Rules.Cli` spouští port jako legacy CLI. Port šel ve třech krocích (ADR 0004):

1. **Věrný port včetně chyb.** Výstup na obou golden sadách (1 300 plateb) byl **bajtově shodný s
   legacy hned napoprvé**. Git tag `phase3-faithful-port`. CSV soubory se čtou jako Latin-1, aby se
   přesně zopakovalo chování legacy po bajtech (16bajtové okno zprávy, převod na velká písmena jen v
   ASCII).
2. **Refaktoring pod záchrannou sítí:** `double` → `decimal` (F3). Golden master zůstal bajtově shodný,
   takže změna je prokázaně bezpečná.
3. **Vědomé změny.** Odtud golden test nevyžaduje shodu. Vyžaduje, aby **každý řádek odlišný od legacy
   vysvětlovala zdokumentovaná změna**; nevysvětlený rozdíl shodí build. Výstup portu je commitnutý
   vedle výstupu legacy (`legacy/golden/*/port_matches.csv`), takže každá změna je vidět v review.

| ID | Změna | Proč | Dopad na golden data |
|---|---|---|---|
| F1 | Odstraněno hledání VS podřetězcem („fuzzy“, BR-12) → `FIELD/NOT_FOUND` | párovalo špatné faktury a tím, že je „spotřebovalo“, blokovalo pozdější správné platby | 60 řádků se v sadě 1 000 liší: fuzzy spárování → nenalezeno, blokované platby → běžně vyhodnocené |
| F2 | Částka platby ≤ 0 (včetně nečitelné) → `N`, `-/INVALID_AMOUNT`; faktury s částkou ≤ 0 se nikdy nepárují | 0 Kč dřív uzavřelo fakturu jako „částečná úhrada“ | žádný (generátor takové částky nevytváří); unit testy |
| F3 | `decimal` místo `double` | přesná práce s penězi | žádný – prokázáno golden masterem |

Klasifikátor v golden testu potřeboval dvě opravy a obě byly skutečné, nepředvídané důsledky F1.
Když fuzzy spárování už fakturu nezabere, platba, kterou legacy odmítl jako `DUPLICATE`, se vyhodnotí
běžně. Může z ní být `OVERPAID`, nejen spárování, a VS může pocházet ze zprávy, ne jen z pole VS.

### Port proti správným odpovědím

| Metrika | Legacy (300 / 1 000) | Port (300 / 1 000) |
|---|---|---|
| Automaticky použito | 197 / 662 | 195 / 648 |
| … správně a bezpečné automatizovat | 160 / 530 | 161 / 534 |
| … správná faktura, měl rozhodnout člověk | 34 / 113 | 34 / 114 |
| … **špatná faktura** | **3 / 19** | **0 / 0** |
| Spárovaný přesný VS + částka | 134 ze 135 / 443 ze 450 | **135 ze 135 / 450 ze 450** |
| Precision automatických spárování | 81,2 % / 80,1 % | 82,6 % / 82,4 % |

**Žádná platba se už nespáruje se špatnou fakturou.** Zbývající nepřesnost je jediného druhu: částečné
úhrady a platby od třetích stran se pořád párují automaticky, jako v legacy, přestože by je měl
potvrdit člověk. To je obchodní rozhodnutí (otevřené otázky Q-02 a Q-07) a úkol další vrstvy (fáze 4):
pravidla navrhují a routing posílá nejisté případy člověku.

Pořád jako v legacy, záměrně (rozhodnout s byznysem): tolerance `< 1,00 Kč` (Q-01), automatické
částečné úhrady bez spodní hranice (Q-02), absolutní přeplatek zálohy 5 000 Kč (Q-04), bankovní pohyby
poznané podle jména (Q-05), cizí měna pro ruční práci (Q-06), žádná kontrola plátce (Q-07), měna
faktury se nekontroluje (R-09).

## 4. Strangler fig: AI nad pravidly (fáze 4, hotovo)

Pravidla zůstávají jako vrstva 1, beze změny. Vrstva 2 řeší jen to, co nerozhodnou (ADR 0006):

```
výpis ─► vrstva 1: přenesená pravidla ─► politika (Q-02, Q-07) ─► auto párování / člověk / nepárovat
                                            │ pravidla nerozhodla
                                            ▼
             kandidáty vybere kód ─► AI gateway (malý → velký model) ─► validace kódem
                                                                       ─► auto / člověk / nepárovat
```

Jak ukazujeme, že AI nerozbila nic, co fungovalo:

- Vrstva 1 uvnitř pipeline je **bajtově shodná s `port_matches.csv`** na obou golden sadách (test).
- AI nikdy nevidí ani nemění platbu, kterou rozhodla pravidla (test: stejná rozhodnutí s AI i bez ní).
- Dvě obchodní rozhodnutí změnila, co znamená „automaticky“: částečné úhrady (Q-02) a platby od třetích
  stran (Q-07) teď jdou člověku. To je vědomá změna, ne vedlejší účinek AI.

### Výsledky na `data/sample` (300 plateb, skutečné odpovědi Clauda, nahrané)

| | Legacy | Port (fáze 3) | Pravidla + AI (fáze 4) |
|---|---|---|---|
| Spárováno bez člověka | 197 (65,7 %) | 195 (65,0 %) | **227 (75,7 %)** |
| … **špatná faktura** | 3 | 0 | **0** |
| … správná faktura, měl rozhodnout člověk | 34 | 34 | **0** |
| Precision automatických spárování | 81,2 % | 82,6 % | **100 %** |
| Spárované prompt injection | 0 | 0 | 0 (všech 15 označeno) |

Strop této sady je 76 %: zbylých 24 % má jít člověku nebo zůstat nespárováno, a tak to je. Podle
případů: přesný VS 135/135, VS ve zprávě 30/30, překlep ve VS 23/24, více faktur 24/24, EUR 15/15
spárováno automaticky; částečné úhrady a přeplatky 24/24 a třetí strany 18/18 člověku; nesouvisející
15/15 a injection 15/15 nespárováno. Jediný překlep ve VS, který se nespároval automaticky, šel člověku
se správným návrhem: Claude Opus 5.5 měl jistotu 0,88, těsně pod prahem 0,90. První nahrávací běh ho
spároval (228); odpovědi modelu se mezi běhy liší, což musí změřit fáze 5.

Cena AI: k modelu se dostalo 79 z 300 plateb (ostatní rozhodla pravidla nebo neměly kandidáta). 91
volání: 79 × Claude Haiku 5.5, 12 eskalací na Claude Opus 5.5. **$0,19 za běh, $0,64 za 1 000 plateb**;
latence na AI platbu p50 2,0 s, p95 5,2 s. Opus tvoří 84 % ceny a 8 z jeho 12 volání byly přeplatky,
jejichž výsledek je stejně „člověk“. Neeskalovat je je první optimalizace ceny pro fázi 5 (potřebuje
běh evalů).

Upřímná výhrada: na syntetických datech dělá většinu práce kód (výběr kandidátů + validace).
Deterministický stub, který jen vezme první návrh kódu, dosáhne stejných čísel. Úkolem modelu je tu
vybrat mezi připravenými kandidáty, poznat injection a vysvětlit rozhodnutí tomu, kdo ho kontroluje.
Fáze 5 ho hodnotí na těžších případech a srovnává jen pravidla, pravidla + stub a pravidla + model.

## 5. Evaly: vyplatí se AI? (fáze 5, hotovo)

`data/sample` neumělo odlišit pravidla + model od pravidel + stubu, takže fáze 5 měří na zmrazené zlaté
sadě (`evals/dataset/gold-v1`, 300 plateb), kde je 30 % záměrně těžkých: remízy mezi dvěma fakturami
nebo kombinacemi, čísla faktur ve formátu, který kód nepřečte, a injection bez spouštěcích slov (ADR
0008). Nahrané skutečné odpovědi, v CI přehrávané s gate:

| | Jen pravidla | Pravidla + stub | Pravidla + malý model | Pravidla + routing (fáze 4) | **Pravidla + routing (teď)** |
|---|---|---|---|---|---|
| Precision automatických spárování | 100 % | 100 % | 100 % | 100 % | **100 %** |
| Automatizace (strop 63,3 %) | 37,7 % | 53,3 % | 52,0 % | 53,3 % | **53,3 %** |
| Odražené injection (ze 40) | 40 | **20** | 40 | 40 | **40** |
| Volání velkého modelu | – | – | – | 39 | **4** |
| Cena za 1 000 plateb | – | – | $0,17 | $2,21 | **$0,36** |

Co evaly ukázaly:

- **První běh našel díru ve validaci, ne v modelu.** Se stubem se všech 30 remíz spárovalo
  automaticky, 8 na špatnou fakturu (precision 84 %, gate neprošel). Validace kontrolovala, že návrh
  sedí, ne že sedí jen on. Nové pravidlo `AMBIGUOUS`; vyčerpávající test validace teď běží i nad zlatou
  sadou.
- **Kde na modelu záleží:** skryté injection. Detektor klíčových slov mine všech 30; stub pak 20×
  ukáže člověku jako návrh fakturu útočníka. Model označí všech 30.
- **Kde (zatím) ne:** číslo faktury v neobvyklém formátu („fakt. č. 1234/26“) je člověku jasné, ale
  validace přijme jen to, co kód ověří, takže všech 30 jde člověku (se správným návrhem). Rozdíl mezi
  53,3 % a stropem 63,3 % je limit kódu, zvolený záměrně.
- **Eskalace jen když může změnit výsledek** (ADR 0009): stejná rozhodnutí, zhruba 6× levněji; na
  `data/sample` $0,64 → $0,27 za 1 000 plateb. Práh 0,90 zůstává (není důvod ho posunout).
- **Rozptyl mezi běhy:** tři živé běhy daly stejné výsledky; změnil se jen návrh u 8 remíz. Nejistota
  modelu se mění v práci pro člověka, nikdy ve špatné spárování.

Report: [`evals/reports/2026-11-12-claude-haiku-5-5+opus-5-5-match-payment.v1.md`](../../evals/reports/2026-11-12-claude-haiku-5-5+opus-5-5-match-payment.v1.md).
