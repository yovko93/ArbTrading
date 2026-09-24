Arbitrage Trading - portable Windows x64 snapshot

Requires a supported Windows x64 OS. No installed .NET runtime, SDK, Visual
Studio, Node.js, or source checkout is required. Both applications carry their
own runtime. See the signing mode below and in Settings. Windows may show a trust warning.
Verify the ZIP SHA-256 against the supplied .sha256 before extracting.

Extract the entire ZIP. Launch Arbitrage.Desktop.exe. Use the explicit local
backend Start button when ready. Desktop does not start the backend on launch.
Settings shows package integrity, build identity, paths, and database startup.
Start verifies the package, safely migrates local storage, then starts backend.

User data is protected per Windows user under %LOCALAPPDATA%\ArbitrageTrading
(backend, runtime, desktop). The extracted package contains no user database
or credentials. Do not copy credentials into this folder.

Paper simulation is available. Automatic Paper requires explicit arming in
Trading. Live order submission is unavailable. First run does not initialize
paper funds, start feeds/monitoring, arm automation, or start a campaign.

To upgrade: stop backend, extract the new ZIP into a new folder, launch Desktop,
then explicitly Start. Existing state is retained. Pending schema upgrades get
a consistent private backup under backend\backups\schema (latest 5 retained).
Only one backend can own the profile. Older versions may reject upgraded schema;
there is no automatic downgrade. Preserve backups for operator-led recovery.

SHA-256 checks package integrity. Authenticode binds signed executable content
to a publisher identity. A ZIP itself is not Authenticode-signed. Edge and
SmartScreen reputation may still warn or block a ZIP even with signed EXEs.
Never disable browser security, SmartScreen or Defender to run this snapshot.
Test signatures are not production publisher trust and must not be installed.
