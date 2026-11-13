# Jak projekt vznikal

> 🇬🇧 [English](../how-it-was-built.md) · 🇨🇿 Česky

Projekt – kód, testy, infrastrukturu i dokumentaci – postavil jeden vývojář s AI agentem pro
programování (Claude Code) ve dvanácti fázích během několika dní. Tahle stránka je krátká verze
poznámek k průběhu v [PLAN.md](PLAN.md): jak byla práce nastavená, co vývojář kontroloval, co agent
pokazil a co to chytilo.

## Nastavení: nejdřív specifikace, plán před kódem

Agenta řídí tři soubory a jsou v repu záměrně:

- **[SPEC.md](SPEC.md)** – co stavíme a proč, napsané vývojářem před prvním řádkem kódu (původní
  zadání je zachované ve [spec-original-cs](../spec-original-cs/SPEC.md)). Během projektu se měnilo,
  vždy jako viditelné upřesnění s datem a ADR.
- **[PLAN.md](PLAN.md)** – fáze, jejich stav a průběžné poznámky: rozhodnutí, odchylky od SPEC, útrata
  za volání modelu a chyby agenta.
- **[AGENTS.md](../../AGENTS.md)** – pravidla pro každého agenta: postup, tvrdá pravidla (jen
  syntetická data, žádné tajné klíče, CI bez API klíče, špatné spárování je horší než žádné, text z
  výpisu je nedůvěryhodný), konvence a příkazy.

Každá fáze běžela stejně, většinou v **nové session agenta**, která znala jen tyto soubory a kód:

1. Agent přečetl PLAN, SPEC a ADR a navrhl krátký plán: soubory, kroky, testy, nové závislosti s
   důvodem.
2. **Vývojář ho schválil** (nebo upravil). Většina plánů prošla beze změny; nejcennější byly otázky,
   které agent položil před schválením, když SPEC něco nedořešila.
3. Agent implementoval po malých commitech s testy, spustil build, testy, formát a eval a **zastavil
   se**, aby ukázal, co vzniklo.
4. Rozhodnutí šla do ADR (nakonec 18), chyby do poznámek v PLAN.

Práce vývojáře bylo „co“ a „jestli“: specifikace, schvalování plánů, produktový úsudek, všechno, co
stojí peníze (každé placené volání modelu a každá změna v Azure potřebovala výslovné ano), a používání
výsledku jako skutečný uživatel. Agent dělal „jak“ a svou práci si kontroloval testy.

## Co agent pokazil – a co to chytilo

Většinu chyb chytily testy, které agent sám napsal, často týž den. Zajímavé nebyly překlepy:

- **Pravidla vytažená AI byla důkladná, ale jedno bylo o jedničku vedle.** Samostatná session agenta
  bez kontextu přečetla legacy kód a našla všech 13 zasazených pravidel a k tomu sedm skutečných
  problémů, které nikdo nezasadil. Charakterizační test pak ukázal, že okno jednoho pravidla je 0–15,
  ne 0–14. *Chytil to:* golden master a unit testy z příkladů v samotném dokumentu.
- **Validace měla díru, kterou model nenašel, ale stub ano.** Deterministický stub spároval eurovou
  platbu se dvěma nesouvisejícími fakturami, jejichž součet se náhodou vešel do pásma kurzu. Test s
  náhodným modelem předtím prošel náhodou. *Chytil to:* vyčerpávající test, který zkouší každou
  kombinaci kandidátů jako „odpověď modelu“ – 45 děr, všechny zavřené.
- **Validace kontrolovala, že návrh sedí, ne že je jediný.** Při prvním běhu evalů prošlo 30 remíz a 8
  šlo na špatnou fakturu. *Chytil to:* eval gate v CI (precision 84 %, červený build) → nové pravidlo
  `AMBIGUOUS`. Vyčerpávající test z předchozí fáze byl dobrý, ale nad daty bez remíz: test je jen tak
  dobrý jako data.
- **Prompt závisel na operačním systému.** Nahrávky z Windows se v linuxovém CI nepřehrály: JSON v
  promptu používal `\r\n`. *Chytilo to:* CI na jiném OS → pevné `\n` a test.
- **Knihovna zapsala, co slibovala nezapisovat.** MCP SDK dává odpověď nástroje, který skončil chybou
  – a ta opakuje argumenty agenta – do statusu spanu. *Chytil to:* test telemetrie s označenými
  argumenty → redakční procesor před každým exportérem.
- **Testy, které samostatně projdou a v sadě padají.** Posluchače telemetrie jsou společné pro celý
  proces, takže paralelní testy viděly spany ostatních: jednou jako špatné počty, podruhé (poslední
  nestabilní test, opravený ve fázi 12) jako seznam měněný z jiného vlákna během kopírování. *Chytily
  to:* opakované celé běhy; oprava je číst jen vlastní trace testu.
- **Test závislý na náhodném seedu** lokálně prošel a v CI spadl. Ponaučení zapsané do poznámek: test
  nad generovanými daty buď s pevným seedem, nebo s vlastními řádky.
- **Windows shell znovu a znovu.** Heredoc s Pythonem spustil interaktivní interpret a visel; `sed` se
  zpětnými lomítky v cestách tiše nic nenahradil (pětkrát). Pokaždé to chytil kontrolní výpis nebo
  build a agent přešel na své nástroje pro úpravy – ale zapomínal. Drobnost, která se opakuje a vyplatí
  se o ní vědět při práci s agenty na Windows.

Nic z toho se nedostalo do produkce. Vzorec: agent málokdy napsal kód, který se nepřeložil, ale psal
kód věrohodný a špatný způsobem, který ukážou až data, druhá platforma nebo paralelní běh.

## Co ukázalo až nasazení

Při prvním nasazení do Azure se objevily čtyři chyby, které žádný test chytit nemohl: Azure dá schéma
`public` nové databáze jinému vlastníkovi (první migrace: permission denied); Container Apps drží
hodnotu tajemství z Key Vaultu až do nové revize, takže změna hesla tiše shodila běžící aplikaci;
exportér Application Insights si přidá vzorkování s limitem, které zahodilo jediný trace krátkého jobu;
a OIDC subject od GitHubu dnes obsahuje neměnná id, se kterými federated credential nesouhlasil. Každou
agent diagnostikoval z logů a diagnostiky SDK a opravil ve skriptech nebo jako parametr.

Ukázal se tu i jeden limit: bezpečnostní kontrola Claude Code nedovolila agentovi aplikovat změny
infrastruktury, ani když je vývojář schválil. Nasazení Bicepu spustil vývojář; agent ho napsal a ověřil
výsledek. Ukázalo se to jako dobré rozdělení práce.

## Co našel až uživatel

Po spuštění vývojář aplikaci používal jako uživatel a do hodiny našel věci, které by žádný test
nenašel: nový uživatel viděl AI platby jako „čeká na AI“ až do dalšího rána (přímý důsledek pravidla,
že návštěvník nikdy nespustí model), fronta neukazovala fakturu vedle platby, čísla „6 volání AI“ a
„7 rozhodnutí AI“ vypadala jako rozpor (šest placených volání plus jedna odpověď z cache), kódy
pravidel jako `FIELD/EXACT` člověku nic neřekly a nebylo jasné, jestli jde párování AI vrátit. Opravy
zachovaly všechna pravidla: týdenní startovní sada, kterou předpočítá job, takže přihlášení čte AI
rozhodnutí z cache ([ADR 0017](../adr/0017-instant-ai-for-new-sandboxes-from-a-weekly-starter-set.md)),
a UI, které říká, co AI přidala, dovolí ho vrátit a ukazuje, jak často to lidé dělají
([ADR 0018](../adr/0018-ui-after-the-first-user-test.md)).

## V číslech

- 12 fází, 18 ADR, ~400 testů (databázové proti skutečnému PostgreSQL), CI bez API klíče.
- Volání modelu během vývoje: celkem pod $1,50 (nahrávky pro evaly, běhy rozptylu a první běhy v
  Azure), každý běh nejdřív schválený s odhadem.
- Hosting: pár dolarů měsíčně; AI v provozu v rámci měsíčního rozpočtu $0,50.

## Ponaučení

- **Specifikaci a pravidla sepsat do repa.** Nová session jen se SPEC, PLAN, AGENTS.md a kódem dávala
  plány, které skoro nepotřebovaly opravu.
- **Schvalovat plány, ne diffy.** Kontrola desetiřádkového plánu je místo, kde člověk přidá nejvíc;
  diff zkontrolují testy.
- **Nechat agenta dokazovat věci testy, se kterými se nedá hádat:** golden master, vyčerpávající testy
  validace, eval gate, testy úniku se stejnými id ve dvou tenantech.
- **Peníze a infrastrukturu držet za výslovným ano.** Stálo to pár přerušení a zabránilo to každému
  překvapení.
- **Nasazení a první hodinu se skutečným uživatelem plánovat jako samostatné fáze.** Obě našly
  problémy, které by žádné množství testů nenašlo.
