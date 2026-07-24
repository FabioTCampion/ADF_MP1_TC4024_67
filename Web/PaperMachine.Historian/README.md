# Paper Machine Historian

Aplicação independente para aquisição ADS, armazenamento local e consulta do histórico da máquina de papel.

## Escopo da primeira entrega

A aplicação é estritamente **somente leitura** no PLC. Ela monitora apenas:

- `.paperMachineHmiStatus` (`PaperMachine_TypeHmiStatus`);
- `.paperMachineHmiCommands` (`PaperMachine_TypeHmiCommands`);
- `.paperMachineHmiAlarms` (`PaperMachine_TypeHmiAlarms`).

O nome técnico é `PaperMachine.Historian` e o nome exibido é **Paper Machine Historian**.

## Destino ADS

| Item | Valor inicial |
| --- | --- |
| AMS Net ID | `192.168.100.1.1.1` |
| IP remoto/rota | `10.8.0.4` |
| Porta | `851` (PLC Runtime 1) |

O IP é uma referência operacional. O cliente conecta pelo AMS Net ID usando a rota configurada no TwinCAT Router da máquina que executa o Historian.

## Banco local

O SQLite cria automaticamente:

```text
src/PaperMachine.Historian.Web/data/PaperMachineHistorian.db
```

O arquivo, seu WAL e arquivos temporários estão ignorados pelo Git. O banco usa:

- `StatusSnapshots`: payload completo de status a cada 10 segundos;
- `StatusChanges`: uma linha por campo de status alterado;
- `CommandEvents`: mudanças observadas na estrutura de comandos;
- `AlarmEvents`: ativação, normalização e duração;
- `AdsCommunicationEvents`: conexão e falhas de aquisição;
- `ApplicationUsers`: usuários locais e hashes de senha;
- `SchemaMigrations`: versão aplicada ao banco.

Datas são armazenadas em UTC. Alarmes encontrados ativos na primeira leitura ficam marcados como `ActiveAtStartup`, pois o horário real de ativação anterior ao início do serviço é desconhecido.

## Executar

Pré-requisitos:

- .NET SDK 10;
- Node.js 24 ou superior para alterar/recompilar o frontend;
- TwinCAT ADS Router com a rota do PLC já configurada;
- acesso TCP/ADS ao equipamento remoto.

Na pasta do projeto:

```powershell
dotnet restore PaperMachine.Historian.slnx
cd src/papermachine-web-client
npm ci
npm run build
cd ../..
dotnet run --project src/PaperMachine.Historian.Web
```

Abra `http://localhost:5088`.

No primeiro acesso, a tela de configuração solicita a criação do administrador local.
A senha deve possuir pelo menos oito caracteres.
O frontend compilado também fica versionado em `wwwroot`, permitindo executar a
aplicação diretamente quando não houver alterações no cliente React.

Para validar sem comunicar com o PLC:

```powershell
dotnet test PaperMachine.Historian.slnx
```

Os testes não escrevem no PLC. Um banco SQLite temporário é usado no teste de persistência.

## Configuração

Os valores iniciais ficam em
`src/PaperMachine.Historian.Web/appsettings.json`.

Configurações relevantes:

- `Ads:AmsNetId`;
- `Ads:RemoteIp` (documentação da rota);
- `Ads:OperationTimeoutMilliseconds`;
- `Historian:PollIntervalMilliseconds`;
- `Historian:StatusSnapshotIntervalSeconds`;
- `Historian:MappingVersion`;
- `Database:FilePath`.

Mudanças na estrutura PLC devem incrementar `Historian:MappingVersion`.

## API inicial

- `GET /health`;
- `GET /api/runtime`;
- `GET /api/current`;
- `GET /api/history/status`;
- `GET /api/history/status-changes`;
- `GET /api/history/motors`;
- `GET /api/history/commands`;
- `GET /api/history/alarms`.

Os históricos aceitam `fromUtc`, `toUtc` e `limit`. Alarmes também aceitam `active=true|false`.
O endpoint de motores aceita `fromUtc`, `toUtc` e `maxPoints` entre 100 e 2.000,
descobre os pares de velocidade/torque do status e limita a consulta a 31 dias.
As APIs do Historian exigem autenticação por cookie; `/health` e o fluxo inicial de autenticação permanecem públicos.

## Limites desta versão

- não escreve comandos no PLC;
- mudança em `paperMachineHmiCommands` significa **comando observado**, não confirmação de execução;
- os gráficos usam os snapshots de status gravados a cada 10 segundos e não representam picos mais rápidos;
- campos sem unidade inequívoca no DUT são exibidos como `unidade PLC`;
- ainda não há relatórios PDF, retenção automática ou agregação;
- a administração completa de usuários e grupos será incorporada em uma próxima evolução.
