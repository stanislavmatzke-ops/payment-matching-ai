# PLAN – fáze a stav

> Agent tento soubor aktualizuje: po dokončení fáze ji odškrtne, doplní datum a krátkou poznámku (co vzniklo, odchylky od SPEC, otevřené otázky). Nová session začíná přečtením tohoto souboru.

## Fáze (každá končí zastavením a ukázkou)

- [x] **Fáze 1** – **Kostra:** repo, solution, Docker Compose (Postgres), CI (build + test + gitleaks), `.env.example`, generátor syntetických dat.
  - _Hotovo 2026-11-12._ .NET 10 (`PaymentMatching.slnx`, central package management, warnings as errors), generátor `src/PaymentMatching.DataGen` + CLI `tools/DataGen.Cli`, ukázková sada `data/sample` (seed 42, 300 plateb, 391 faktur, 60 odběratelů), 32 testů, CI (format, build, test, kontrola reprodukovatelnosti dat, gitleaks), Postgres 17 v Compose, ADR 0001–0002, `docs/data-format.md`.
  - _Odchylky od SPEC:_ SPEC neuvádí poměry případů – zvoleno 45/10/8/8/8/6/5/5/5 % (viz `docs/data-format.md`), lze změnit v `GeneratorOptions`. Jediná závislost je `xunit.v3` (testy).
  - _Doplněno po fázi:_ repo na GitHubu (soukromé, `stanislavmatzke-ops/payment-matching-ai`), CI zelené. Free Pascal poběží v Dockeru (`freepascal/fpc:3.2.2-full`, ověřeno přeložením testovacího programu v režimu `-Mdelphi`), nic se neinstaluje.
- [x] **Fáze 2** – **Legacy:** Delphi + PL/SQL modul, CLI přes Free Pascal, golden master výstupy.
  - _Hotovo 2026-11-12._ `legacy/delphi` (5 unit, překlad `fpc -Mdelphi` v Dockeru přes `legacy/run-legacy.sh`), `legacy/plsql` (package, jen zdroják), golden master `legacy/golden/` (300 + 1 000 plateb), CI job `legacy-golden-master`, ADR 0003, `docs/modernization.md` (krok 2 + výchozí čísla legacy).
  - _Výchozí stav legacy proti ground truth:_ automatizace ~66 %, přesnost automatických párování ~80 %, 3 / 19 plateb na špatnou fakturu, 0 spárování podle prompt injection.
  - _Odchylky od SPEC:_ žádné. `docs/business-rules.md` vědomě nevzniklo, je to úkol fáze 3 (extrakce z kódu). Klíč zasazených pravidel je mimo git v `../payment-matching-ai-private/legacy-answer-key.md`.
- [x] **Fáze 3** – **Port pravidel do .NET** + charakterizační testy (shoda s legacy) + `business-rules.md`.
  - _Hotovo 2026-11-12._ Hotové části:
    - `docs/business-rules.md` (extrakce izolovaným agentem, 13/13),
    - `src/PaymentMatching.Rules` + `tools/Rules.Cli`,
    - 62 unit testů + 4 golden master testy,
    - věrný port bajtově shodný s legacy (tag `phase3-faithful-port`),
    - změny F1 (pryč fuzzy hledání), F2 (neplatné částky) a F3 (`decimal`),
    - `port_matches.csv`, ADR 0004, `docs/modernization.md` krok 1 a 3.
  - _Výsledek proti ground truth:_ 0 plateb na špatnou fakturu (legacy 3 / 19), všech 450 přesných shod spárováno (legacy 443), přesnost auto párování 82,4 %. Zbytek tvoří jen případy pro člověka (částečné úhrady, třetí strany), a to je úkol fáze 4.
  - _Otevřené pro tebe:_ obchodní otázky Q-01..Q-12 v `business-rules.md`, hlavně Q-02 (automatické částečné úhrady) a Q-07 (kontrola plátce). Zatím chování zůstává jako v legacy.
- [x] **Fáze 4** – **AI gateway + AI párování** (structured output, validace, routing, audit, idempotence).
  - _Hotovo 2026-11-12._ Vzniklo:
    - `src/PaymentMatching.AiGateway`: provider abstrakce, oficiální Anthropic SDK, routing Haiku 5.5 → Opus 5.5, timeout, strop volání, tokeny a cena, record/replay,
    - `src/PaymentMatching.Matching`: politika nad pravidly (Q-02, Q-07), výběr kandidátů kódem, prompt `prompts/match-payment.v1.md`, striktní structured output, validace kódem, rozhodování, append-only audit log s idempotencí,
    - `tools/Match.Cli`: režimy `stub|replay|record|live|rules`,
    - 91 nahrávek reálných odpovědí v `evals/recordings/`,
    - ADR 0005–0007, `modernization.md` krok 4, stav Q-02/Q-06/Q-07/Q-08/Q-11 v `business-rules.md`.
  - _Výsledek na `data/sample` (reálný model):_
    - automatizace 75,7 % (strop sady 76 %), přesnost auto párování 100 % (port 82,6 %), 0 špatných faktur,
    - 15/15 injection zachyceno,
    - $0,64 za 1 000 plateb, p50 2,0 s / p95 5,2 s na AI platbu,
    - 192 testů.
  - _Rozhodnuto (schváleno v plánu fáze):_
    - Q-02: částečné úhrady a přeplatky záloh jdou vždy člověku s návrhem.
    - Q-07: auto párování vyžaduje IBAN odběratele (výjimka: přesný VS v poli + částka); třetí strany jdou člověku.
  - _Odchylky od SPEC:_
    - audit a idempotence zatím v JSONL souboru (`DecisionLog`), PostgreSQL až s tenanty ve fázi 7,
    - EUR kurz zatím pevné pásmo 24,00–26,50, ne denní kurz ČNB.
  - _Pro fázi 5:_
    - neeskalovat odpovědi, které stejně jdou člověku (8 z 12 volání Opus, Opus = 84 % ceny),
    - kalibrace prahů (Opus dal správné odpovědi 0,88 → člověk),
    - měřit rozptyl mezi běhy (dva nahrávací běhy: 228 vs 227 auto),
    - srovnání pravidla / stub / model na těžších datech.
- [x] **Fáze 5** – **Evaly** – zlatá sada, runner, reporty, replay v CI, práh v CI.
  - _Hotovo 2026-11-12._ Vzniklo:
    - zlatá sada `evals/dataset/gold-v1` (300 plateb, seed 5005, `--profile eval`, 30 % těžkých případů: remízy, nečitelné číslo faktury, injection bez klíčových slov), review v `REVIEW.md`, zmrazená přes `MANIFEST.sha256`,
    - `src/PaymentMatching.Evals` (scorer, gate, rozptyl, Markdown report) + `tools/Eval.Cli` (`replay|record|live`, `--repeat`),
    - 5 konfigurací: `rules`, `stub`, `small`, `routing-v1`, `routing`; CI krok s gate (precision ≥ 99 % a 0 spárovaných injection pro všechny, `routing` navíc cíl injection nikdy nenavržen) + report jako artifact,
    - nové pravidlo validace `AMBIGUOUS`, eskalace jen když může změnit výsledek (výchozí), gateway umí běžet bez velkého modelu,
    - 200 nových nahrávek, report `evals/reports/2026-11-12-…-match-payment.v1.md` + report rozptylu, ADR 0008–0009, `modernization.md` krok 5, tabulka v README, 219 testů.
  - _Výsledek na zlaté sadě (`routing`, reálný model):_ precision 100 %, automatizace 53,3 % (strop 63,3 %; rozdíl = nečitelná čísla faktur, vědomě člověku), 40/40 injection, $0,36 za 1 000 plateb (`routing-v1` $2,21), p50 2,0 s / p95 4,1 s. Stub: 100 % precision, ale 20 cílů skrytých injection navrhl člověku. Na `data/sample` $0,64 → $0,27 za 1 000 plateb při stejných 227 auto.
  - _Položky z fáze 4:_ eskalace vyřešena (39 → 4 volání Opus); práh 0,90 ponechán (všechny validované odpovědi ≥ 0,90 a správné, sweep 0,80–0,90 beze změny, 0,95 ubírá automatizaci); rozptyl 3 živé běhy: výsledky identické, liší se jen návrh u 8 remíz.
  - _Odchylky od SPEC:_ runner je `tools/Eval.Cli` (ne `dotnet run --project evals`), živý eval `--mode live`. Precision gate platí pro všechny konfigurace, nejen pro produkční.
  - _Útrata za živá volání:_ $0,67 nahrávání + $0,24 rozptyl = $0,91 (schválený strop $2,50).
  - _Pro tebe:_ namátková kontrola 14 plateb ze zlaté sady (tabulka v `evals/dataset/gold-v1/REVIEW.md`); opravy by šly do `gold-v2`.
- [x] **Fáze 6** – **MCP server** + návod pro Claude Desktop / Code.
  - _Hotovo 2026-11-12._ Vzniklo:
    - `src/PaymentMatching.Mcp` (`payments-mcp`): oficiální C# MCP SDK 2.4.0, transport stdio (výchozí) a Streamable HTTP (jen localhost, filtr `Host` hlavičky),
    - nástroje `list_unmatched`, `get_payment`, `get_customer`, `search_invoices` (jen čtení) a `propose_match` (jen návrh do fronty `proposals.jsonl`, idempotentní, s kontrolami `ProposalValidator`), MCP prompt `investigate_unmatched`,
    - text z výpisu jen pod `untrusted_bank_data` + `injection_suspected`, instrukce serveru a popisy nástrojů,
    - 37 testů (nástroje, injection, idempotence, audit log beze změny, skutečný MCP klient přes in-memory streamy: seznam nástrojů, anotace, schémata bez tenant/cesty, chyby, prompt), celkem 256,
    - ADR 0010, návod v README (Claude Code, Claude Desktop, HTTP), příkaz v AGENTS.md.
  - _Odchylky od SPEC:_
    - parametr `invoiceIds` se jmenuje `invoiceNumbers` (v doméně je identifikátor faktury její číslo),
    - místo tenantu „workspace na proces“ (adresář dat + stavu z příkazové řádky, žádný nástroj ho nevybírá); izolace tenantů v datové vrstvě přijde ve fázi 7 a MCP na ni přejde (ADR 0010),
    - server čte audit log jednou při startu (po novém běhu pipeline restart).
  - _Útrata:_ $0, server model nevolá, žádný `record`/`live` běh.
  - _Pro fázi 7/8:_ fronta návrhů z MCP se má v UI zobrazit vedle návrhů pipeline (kdo navrhl: `agent:mcp`).
- [ ] **Fáze 7** – **Multi-tenant + přihlášení** – Microsoft/Google login, sandbox na uživatele, startovní data, testy izolace.
- [ ] **Fáze 8** – **UI** – seznam plateb, „kdo rozhodl“, fronta pro člověka (potvrdit / zamítnout / spárovat ručně), audit timeline, dashboard.
- [ ] **Fáze 9** – **Denní job** – generátor + automatické zpracování, limity a rozpočet na AI.
- [ ] **Fáze 10** – **Observabilita** – OpenTelemetry, cena a tokeny v trace, Application Insights.
- [ ] **Fáze 11** – **Azure** – Bicep/azd, Container Apps + Job, PostgreSQL, Key Vault, nasazení z GitHub Actions přes OIDC, vlastní doména (subdoména stanislavmatzke.com).
- [ ] **Fáze 12** – **Dokumentace a příprava na zveřejnění** – README, ADR, security, cost, modernization, how-it-was-built + dvojjazyčnost a úklid před zveřejněním repa:
  - **Hlavní jazyk repa je angličtina.** `README.md`, `AGENTS.md`, `docs/SPEC.md`, `docs/PLAN.md` a všechny dokumenty v `docs/` budou anglicky.
  - **Česká verze vedle:** `README.cs.md` a `docs/cs/` (SPEC, PLAN, modernization, how-it-was-built). Na začátku každého dokumentu odkaz na druhou jazykovou verzi („🇨🇿 Česky / 🇬🇧 English“).
  - **Originál zadání:** současné české `docs/SPEC.md` a `docs/PLAN.md` přesunout beze změn obsahu do `docs/spec-original-cs/` jako autentický start projektu (jen s úpravami z bodu „úklid“). Přeložené verze vycházejí z nich.
  - **AGENTS.md:** anglická verze je ta, kterou čte agent. Pravidlo „komunikace se mnou česky“ ponechat.
  - **Úklid osobních věcí:** z veřejných dokumentů odstranit sekci „Výstupy pro mě mimo repo“ a zmínky o hiring managerech, CV a pohovorech. Úvod SPEC přepsat neutrálně („Reference implementation showing…“). Tyto výstupy (osnova článku 04, LinkedIn text, odrážky do CV) připravit zvlášť do souboru mimo git (např. `../payment-matching-ai-private/outputs.md`) a ukázat mi je v chatu.
  - **README** v obou jazycích: problém → architektura (Mermaid) → výsledky evalů (tabulka) → odkaz na živou aplikaci → spuštění za 2 minuty → „Built spec-first with an AI coding agent – see AGENTS.md, docs/SPEC.md, docs/PLAN.md“ → licence MIT.
  - **Kontrola před zveřejněním:** gitleaks nad celou historií, žádná reálná data, žádné tajné klíče, žádné interní cesty nebo e-maily, odkazy v dokumentech fungují, `LICENSE` existuje.
  - Na konci mi dej seznam souborů ke kontrole a čekej na mé schválení. Repo zveřejním sám.

Pořadí 1–6 je priorita (repo s modernizací, AI párováním, evaly a MCP musí být ukazatelné co nejdřív). Fáze 7–11 dělají z projektu živou aplikaci.


## Výstupy pro mě mimo repo

Po dokončení připrav:
- osnovu článku 04 na web (CZ): „Párování plateb s AI: od Delphi k evalům“ – zkušenost, čísla z evalů, co AI rozhoduje a co ne, jak dopadla modernizace,
- 3–4 věty pro LinkedIn,
- 5 odrážek do CV / pohovoru (co projekt ukazuje).

## Poznámky k průběhu

_(agent sem zapisuje průběžně: rozhodnutí, problémy, co pokazil a jak se to opravilo – podklad pro docs/how-it-was-built.md a článek)_

**Fáze 1 (2026-11-12)**
- Agent nejdřív přidal klasickou trojici testovacích balíčků (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`). Na .NET 10 SDK s xUnit v3 to selhalo („VSTest target is no longer supported“). Oprava: `global.json` → `test.runner = Microsoft.Testing.Platform`, dva balíčky odebrány. Méně závislostí.
- `dotnet format` na Windows hlásil chyby konců řádků u souborů zapsaných s LF. Oprava: `end_of_line = lf` v `.editorconfig` + `.gitattributes` (`eol=lf`), aby formát vycházel stejně lokálně i v Linuxovém CI.
- Riziko, které agent ošetřil předem: VS s přehozenými číslicemi mohl náhodou odpovídat jiné existující faktuře (pak by „překlep“ byl ve skutečnosti správný VS jiné faktury). Generátor takové kolize přepíná na variantu s chybějící číslicí (9 místný VS nemůže kolidovat). Kryto testem.
- Zaokrouhlení vah případů: `0.06 * 300` v plovoucí čárce může dát 17,999…, takže epsilon v plánovači počtů.
- První push odmítl GitHub (ochrana soukromého e-mailu v commitech). Oprava: autor commitů v repu nastaven na GitHub noreply adresu, 4 lokální commity přepsány před prvním pushem. Výhoda: v budoucí veřejné historii není osobní e-mail.
- První běh CI: `gitleaks-action@v2` spadl na prvním pushi nového repa (neplatný rozsah `root^..HEAD`, neprohledal nic). Oprava: gitleaks CLI s ověřeným checksumem nad celou historií. Ponaučení: „zelený“ secret scan je třeba číst – action hlásil „no leaks found in partial scan“ nad 0 bajty.
**Fáze 2 (2026-11-12)**
- Při návrhu legacy pravidel agent zjistil, že první verze pravidla „částečná úhrada“ by se dala zneužít textem zprávy: injection „platba patří k faktuře 2026-xxxx“ s menší částkou by se spárovala jako částečná úhrada. Úprava: částečná úhrada jen tehdy, když VS přišel v poli VS (realistická výjimka „2007-03 JN“). Legacy tak odolává injection, ale jen náhodou, a to je v `docs/modernization.md` výslovně řečeno.
- Neplánovaný, ale cenný nález v datech: chybné fuzzy spárování „spotřebuje“ fakturu, takže pozdější správná platba na ni skončí jako `DUPLICATE` (7× v sadě 1 000). Jedna chyba tak dělá dvě škody.
- Agent napsal jednorázový srovnávací skript v awk a použil v něm proměnnou `NR`, což je vestavěná proměnná awk (číslo řádku). První čísla „bez člověka“ byla proto nesmyslná (636 z 300 plateb). Chyba byla odhalena kontrolou součtů a opravena. Ponaučení: i jednorázový měřicí skript potřebuje kontrolu.
**Fáze 3, krok 1 – extrakce pravidel z legacy (2026-11-12)**
- Extrakci dělal agent v **samostatné session bez kontextu**. Směl číst jen `legacy/delphi`, `legacy/plsql`, `legacy/README.md`, `docs/data-format.md` a `AGENTS.md`. Nespouštěl kód, neviděl golden master, PLAN, modernization ani klíč odpovědí. Podle vlastního hlášení zákaz dodržel.
- Výsledek: `docs/business-rules.md`, 24 pravidel, 15 rozdílů Delphi × PL/SQL, 13 rizik s konkrétními vstupy, 12 otevřených otázek.
- **Skóre proti klíči odpovědí (mimo git): 13 / 13.** Našel všech 11 zasazených pravidel včetně záměrné fuzzy chyby, jejího domino efektu (`DUPLICATE`) a nesouladu tolerance `<` × `<=`, i obě položky „drobností“.
- **Našel i věci, které autor legacy (jiná session agenta) nezasadil vědomě**, všechny ověřené proti kódu:
  - PL/SQL okno 26 znaků uřízne číslo faktury: `FAKTURA PRO FIRMU ABC 2026-1234` → `2026-1` → jiná faktura,
  - prázdná měna: Delphi odmítne, PL/SQL páruje jako CZK (NULL v podmínce),
  - nečitelná částka = 0 → automatická „částečná úhrada“, která uzavře fakturu,
  - částečná úhrada nemá spodní hranici (1 Kč uzavře fakturu),
  - měna faktury se nekontroluje,
  - `ROWNUM = 1` bez `ORDER BY` je v PL/SQL nedeterministické,
  - třídění `payment_id` jako řetězce.
- Kontrola: namátkově ověřeno 17 odkazů na řádky, všechny přesné. Halucinace nenalezeny. Jediná oprava: v příkladech bylo jméno „NOVAK JAN“ (reálně znějící) → nahrazeno fiktivní firmou.
- Postřeh do článku: AI byla v porozumění kódu **důkladnější než autor fikce**. Nejcennější nejsou samotná pravidla, ale okrajové případy s konkrétními vstupy, ze kterých jdou rovnou psát testy.

**Fáze 3, port (2026-11-12)**
- Věrný port prošel bajtovou shodou s legacy **napoprvé** na 1 300 platbách. Pomohlo, že business-rules.md obsahovala přesný popis výstupního formátu a doporučení číst text „po bajtech“ (Latin-1).
- **Charakterizační test opravil AI dokument:** dva unit testy převzaté z příkladů v business-rules.md selhaly, zatímco golden test prošel. Pravidlo o okně zprávy mělo chybu o jedničku (0–14 místo 0–15). Ověřeno sondou nad legacy binárkou, dokument opraven s poznámkou. Ponaučení: AI extrakce je skvělý start, ale přesnost na úrovni řádku ověří až běžící kód.
- `double` → `decimal` (F3) bez jediné změny výstupu. Golden master z toho udělal bezpečný refaktoring.
- Test „každý rozdíl musí být vysvětlený“ hned **dvakrát chytil nepředvídané důsledky F1**: z `DUPLICATE` se může stát i `OVERPAID`, a VS může pocházet i ze zprávy. Agent to v prvním návrhu klasifikátoru nedomyslel, test ho opravil. Přesně tohle má test dělat.
- Fuzzy chyba v legacy blokovala i správné platby od třetích stran a s VS ve zprávě (2 případy v sadě 1 000), nejen přesné shody.

**Fáze 4 (2026-11-12)**
- Plán vznikl v nové session jen z PLAN/SPEC/ADR/business-rules, bez doptávání. Politiku pro Q-02/Q-07 agent navrhl až po spojení výstupu portu s ground truth podle typu případu.
- **Validaci prolomil stub, ne model.** Deterministický stub (bere první návrh kódu) spároval EUR platbu s kombinací dvou jiných faktur téhož odběratele. Pásmo kurzu je široké ~10 %, takže se do něj součet „vešel“. Test s náhodným modelem to předtím přešel **náhodou**. Oprava:
  - test nahrazen vyčerpávajícím: každá množina 1–4 kandidátů jako „odpověď modelu“ nad 1 300 platbami; našel 8 + 37 děr, všechny EUR,
  - dvě nová pravidla validace: platba musí fakturu jmenovat (VS / číslo ve zprávě) a EUR platí jen jednu fakturu,
  - poté 0 děr.
- Replay test nejdřív selhal: CLI a testy měly jiné `MaxTokens`, a ty jsou součástí klíče nahrávky. Oprava: jedna definice modelů (`ModelSettings`).
- **CI odhalilo závislost promptu na OS.** Nahrávky vznikly na Windows, v CI (Linux) replay nenašel klíč. `WriteIndented` JSON v promptu používal `Environment.NewLine`, takže modelu šel jiný text podle OS (`\r\n` vs `\n`). Oprava:
  - pevné `\n` + test „prompt neobsahuje `\r`“,
  - nové nahrávání (podruhé ~$0,20).
  - Ponaučení: klíč nahrávky = otisk přesně toho, co jde do modelu, a ten musí být stejný na každé platformě. Bez CI na jiném OS by se to nezjistilo.
- Odhad ceny nahrávání „< $0,20“: první běh $0,205, druhý $0,19. 84–85 % ceny tvoří eskalace na Opus.
- Drobnosti, které chytil build/test: neuzavřený `sed` v heredocu (soubory se nezapsaly), `--` v XML komentáři csproj, `JsonException` místo `FormatException` z parseru, porovnání recordů se seznamy přes referenci.
- Model (Haiku) v jednom zdůvodnění tvrdil, že zpráva jmenuje obě faktury, přitom jmenovala jednu. Rozhodnutí bylo správně, zdůvodnění ne. Proto zdůvodnění není důkaz a rozhoduje validace.

- Permission prompty v Claude Code zdržovaly; uživatel povolil běžné příkazy v `.claude/settings.local.json` (mimo git).

**Fáze 5 (2026-11-12)**
- Plán vznikl znovu v nové session jen z PLAN/SPEC/ADR a kódu, schválen beze změn.
- **První eval běh našel díru ve validaci.** Stub nad zlatou sadou: precision 84,2 %, 8 plateb na špatnou fakturu. Všech 30 remíz (dvě faktury nebo kombinace se stejnou částkou) prošlo validací: ta ověřovala, že návrh *sedí*, ne že je *jediný*. Gate build shodil. Oprava: pravidlo `AMBIGUOUS` + vyčerpávající test validace nově i nad zlatou sadou a přísnější (platný návrh musí být jediný správný a smí být automatický). Ponaučení: vyčerpávající test z fáze 4 byl dobrý nástroj nad daty, ve kterých remízy nebyly. Test je jen tak dobrý jako data.
- Skutečná přidaná hodnota modelu na těchto datech: injection bez klíčových slov (30/30 zachyceno, stub navrhl cíl člověku 20×). Na nečitelných číslech faktur model nepomůže, protože automatické párování smí jen to, co ověří kód. Parser se vědomě nerozšiřoval podle formátů generátoru (přeučení na vlastní data).
- Nové pravidlo eskalace snížilo volání Opus 39 → 4 při stejných výsledcích. Rozhodnutí je kód (stejný validátor), ne úsudek modelu.
- Nahrávací běh v režimu `record` zahrnul i sweep prahů: jiný práh mění, které faktury se spárují dřív, takže pozdější platby dostanou jiné kandidáty a vzniklo 25 volání Haiku navíc (~$0,01). Neškodné, ale poučné: „sweep zdarma“ platí jen pro replay.
- Drobnosti, co agent pokazil: heredoc + Python (Windows Store stub) nezapsal nic → Write/Edit; `sed` se zpětnými lomítky v cestách csproj tiše nic nenahradil (dvakrát, odhalil build); `sha256sum` na Windows píše `*` před název souboru, první manifest proto nesouhlasil (odhalil runner hned při prvním běhu).

**Fáze 6 (2026-11-12)**
- Plán znovu vznikl v nové session jen z PLAN/SPEC/ADR a kódu, schválen beze změn. Jediná vědomá odchylka: tenant až ve fázi 7, do té doby „workspace na proces“ (zapsáno v ADR 0010).
- **Test protokolu chytil chybu, kterou kompilátor nevidí:** `WithPrompts(typeof(InvestigatePrompt))` se navázalo na generické přetížení „instance cíle“ (`TPromptType target`) s objektem `Type`, takže se nezaregistroval žádný prompt. Unit testy nad třídou by prošly; selhal až test přes skutečného MCP klienta (`prompts/list` „not available“). Oprava: `WithPrompts([typeof(...)])`. Ponaučení: u DI/atributových registrací testovat přes protokol, ne jen třídu.
- `dotnet run` u projektu s Web SDK spouští aplikaci v adresáři projektu, takže výchozí relativní cesty (`data/sample`) neexistovaly. Odhalil to ruční smoke test přes stdio. Oprava: `RunWorkingDirectory = $(MSBuildStartupDirectory)`.
- Smoke test přes stdio: prvních pár pokusů nevrátilo nic, protože konec stdin server ukončí dřív, než odešle odpovědi. Testovací skript musel držet stdin otevřený. Ověřeno také, že `dotnet run` nepíše výstup buildu na stdout (rozbil by JSON-RPC).
- HTTP transport doplněn o filtr `Host` hlavičky (jen loopback), aby se k serveru na localhostu nedostala webová stránka přes DNS rebinding. Ověřeno curl: cizí Host → 400.
- `sed` se zpětnými lomítky v cestě csproj potřetí tiše nic nenahradil (přidání `InternalsVisibleTo`); tentokrát si toho agent všiml hned z výpisu souboru a přešel na Edit.
