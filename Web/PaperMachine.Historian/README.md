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
- `CommandEvents`: eventos ADS `on-change` da estrutura de comandos, independentes
  do ciclo periódico dos snapshots;
- `AlarmEvents`: ativação, normalização, duração, mensagem didática em português
  e contexto C2000 Plus (código, descrição, torque retido e referência do manual);
- `AdsCommunicationEvents`: conexão e falhas de aquisição;
- `ApplicationUsers`: usuários locais e hashes de senha;
- `SchemaMigrations`: versão aplicada ao banco.

Datas são armazenadas em UTC. Alarmes encontrados ativos na primeira leitura ficam marcados como `ActiveAtStartup`, pois o horário real de ativação anterior ao início do serviço é desconhecido.

## Diagnóstico dos drives C2000 Plus

O objeto EtherCAT `603Fh` já entrega o código de erro no PDO. O bloco
`FB_DELTA_C2000_ETC` publica e retém, para cada acionamento:

- código C2000 Plus no momento da falha;
- torque de saída escalonado no mesmo ciclo da falha;
- contador incremental do evento.

Esses valores são espelhados em `.paperMachineHmiStatus`. A aplicação associa o
snapshot ao alarme booleano correspondente, grava os valores brutos no SQLite e
apresenta a descrição em português. Código desconhecido permanece visível em
decimal e hexadecimal e não recebe interpretação presumida.

As ações apresentadas são orientações de diagnóstico. A aplicação continua
somente leitura e nunca executa reset automático no drive ou no PLC.

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
- `GET /api/history/productivity`;
- `GET /api/history/commands`;
- `GET /api/history/alarms`.

Os históricos aceitam `fromUtc`, `toUtc` e `limit`. Alarmes também aceitam `active=true|false`.
O endpoint de motores aceita `fromUtc`, `toUtc` e `maxPoints` entre 100 e 2.000,
descobre os pares de velocidade/torque do status e limita a consulta a 31 dias.
Na interface, os acionamentos são organizados por grupo funcional. Os motores
selecionados por checkbox são comparados como séries no mesmo gráfico de
velocidade e no mesmo gráfico de torque.
As métricas de manutenção usam as quebras confirmadas pela condição de
produtividade: MTTR é a duração média das quebras, MTBF é o tempo produtivo
acumulado dividido pelo número de quebras e MTTF é a duração média dos períodos
produtivos que terminaram em quebra. Lacunas sem amostras não são tratadas como
tempo produtivo.
A análise de correlação relaciona cada quebra a comandos e mudanças discretas
de estado ocorridos entre cinco minutos antes e um minuto depois do seu início.
Ela destaca recorrência temporal, mas não atribui causalidade automaticamente.
As APIs do Historian exigem autenticação por cookie; `/health` e o fluxo inicial de autenticação permanecem públicos.

## Limites desta versão

- não escreve comandos no PLC;
- mudança em `paperMachineHmiCommands` é capturada por notificação ADS imediata,
  mas significa **comando observado**, não confirmação de execução;
- os gráficos usam os snapshots de status gravados a cada 10 segundos e não representam picos mais rápidos;
- correlações de quebra são indícios temporais e precisam de confirmação por
  alarme, diagnóstico do drive ou análise técnica;
- campos sem unidade inequívoca no DUT são exibidos como `unidade PLC`;
- ainda não há relatórios PDF, retenção automática ou agregação;
- a administração completa de usuários e grupos será incorporada em uma próxima evolução.
