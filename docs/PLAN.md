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
- [ ] **Fáze 4** – **AI gateway + AI párování** (structured output, validace, routing, audit, idempotence).
- [ ] **Fáze 5** – **Evaly** – zlatá sada, runner, reporty, replay v CI, práh v CI.
- [ ] **Fáze 6** – **MCP server** + návod pro Claude Desktop / Code.
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

- Permission prompty v Claude Code zdržovaly; uživatel povolil běžné příkazy v `.claude/settings.local.json` (mimo git).
