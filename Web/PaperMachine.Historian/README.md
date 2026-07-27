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

- `TelemetrySamples`: telemetria numérica larga a cada 5 segundos, sem repetir
  nomes de variáveis em cada amostra;
- `TelemetryMinuteAggregates`: médias por minuto para gráficos e métricas de
  períodos longos;
- `StatusSnapshots`: payload completo de diagnóstico a cada 60 segundos;
- `StatusChanges`: somente mudanças discretas (booleanos, estados e códigos);
- `CommandEvents`: eventos ADS `on-change` da estrutura de comandos, independentes
  do ciclo periódico dos snapshots;
- `AlarmEvents`: ativação, normalização, duração, mensagem didática em português
  e contexto C2000 Plus (código, descrição, torque retido e referência do manual);
- `PaperBreakEvents`: início, fim, duração e velocidade das quebras detectadas
  pelo terceiro grupo de secagem;
- `PaperBreakDiagnosticSamples`: janela indexada de T-180 s até T0 com as
  variáveis relevantes de processo, motores, bombas, headbox e estados;
- `PaperBreakDiagnosticSummary`: estatísticas por variável, comparação entre a
  linha de base e os 30 segundos finais e índice de alteração;
- `PaperBreakEvidence`: alarmes, comandos ADS `on-change` e mudanças discretas
  correlacionadas à mesma janela;
- `AdsCommunicationEvents`: conexão e falhas de aquisição;
- `HistorianMaintenanceState`: progresso da migração e última manutenção;
- `ApplicationUsers`: usuários locais e hashes de senha;
- `SchemaMigrations`: versão aplicada ao banco.

Datas são armazenadas em UTC. Alarmes encontrados ativos na primeira leitura ficam marcados como `ActiveAtStartup`, pois o horário real de ativação anterior ao início do serviço é desconhecido.

A página **Análise de quebras** permite filtrar eventos, sobrepor variáveis em
eixos separados por unidade, consultar evidências e registrar a causa validada
pelo analista. O índice de alteração é um auxílio estatístico e não é tratado
como prova automática de causalidade.

A página **Métricas** gera o relatório operacional de produção e quebras em PDF.
O documento usa a velocidade, o sensor de papel do terceiro grupo e
`stockPumpState = 1` (bomba de massa ligada) para calcular produtividade. Usa
`PaperBreakEvents` como fonte das quebras, causas e análises. Períodos acima de
12 horas consultam os agregados de um minuto.

O histórico legado em JSON é convertido progressivamente em agregados por minuto
antes de ser removido pela retenção. A limpeza ocorre em pequenos lotes e só é
habilitada depois de existir pelo menos 24 horas de telemetria no formato novo.
Alarmes, comandos, quebras e usuários não são removidos pela retenção automática.

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

Para gerar o pacote autocontido e instalar como serviço Windows com início
automático, consulte [DEPLOYMENT.md](DEPLOYMENT.md).

Para gerar pacote, tag e GitHub Release com um único comando, consulte
[RELEASE-PROCESS.md](RELEASE-PROCESS.md). O fluxo reutiliza caches com validação
do lockfile, compila o frontend uma vez, evita bloquear o servidor local e mede
o tempo de cada etapa.

Para entender, operar ou reutilizar o atualizador por GitHub Releases, consulte
[UPDATE-SYSTEM.md](UPDATE-SYSTEM.md). O documento inclui fluxo ponta a ponta,
modelo de ameaça, limitações de segurança e checklist de replicação. O guia
[AUTOMATIC-UPDATE-AGENT-HANDOFF.md](AUTOMATIC-UPDATE-AGENT-HANDOFF.md) descreve
a reprodução precisa desse fluxo em outra aplicação.

## Configuração

Os valores iniciais ficam em
`src/PaperMachine.Historian.Web/appsettings.json`.

Configurações relevantes:

- `Ads:AmsNetId`;
- `Ads:RemoteIp` (documentação da rota);
- `Ads:OperationTimeoutMilliseconds`;
- `Historian:PollIntervalMilliseconds`;
- `Historian:TelemetrySampleIntervalSeconds`;
- `Historian:StatusSnapshotIntervalSeconds`;
- `Historian:DiagnosticSnapshotRetentionDays`;
- `Historian:RawTelemetryRetentionDays`;
- `Historian:AggregateRetentionDays`;
- `Historian:StatusChangeRetentionDays`;
- `Historian:CommunicationEventRetentionDays`;
- `Historian:MaintenanceIntervalMinutes`;
- `Historian:MaintenanceBatchSize`;
- `Historian:PaperBreakMinimumSpeedMpm`;
- `Historian:PaperBreakDiagnosticWindowSeconds` (padrão: `180`);
- `Historian:RetentionEnabled`;
- `Historian:MappingVersion`;
- `Database:FilePath`;
- `Reporting:MachineName`;
- `Reporting:MaximumRangeDays` (padrão: `31`);
- `Reporting:MaximumBreaks` (padrão: `500`);
- `Updates:Enabled`;
- `Updates:RepositoryOwner`;
- `Updates:RepositoryName`;
- `Updates:CheckIntervalMinutes`;
- `Updates:AutoDownload`;
- `Updates:WorkingDirectory`;
- `Updates:TokenFilePath`;
- `Updates:UpdaterTaskName`.

Mudanças na estrutura PLC devem incrementar `Historian:MappingVersion`.

## Atualização privada pelo GitHub

O servidor consulta a última **Release estável** do repositório privado
`FabioTCampion/ADF_MP1_TC4024_67`. Ele não executa `git pull`, não recebe
código-fonte e não precisa de Git, Node.js ou SDK .NET. A cada 30 minutos, o
serviço:

1. consulta os metadados da Release usando um token somente leitura;
2. compara a versão da tag com `deployment-state.json`;
3. baixa automaticamente o ZIP e o arquivo `.sha256` quando existe versão nova;
4. valida SHA-256 e a versão do manifesto interno;
5. mantém o pacote em `updates/pending` até um administrador confirmar a
   instalação pela página **Atualizações**.

A aplicação Web apenas grava uma solicitação validada e aciona a tarefa fixa
`CPNTeckPaperMachineHistorianUpdater`. Essa tarefa executa como `SYSTEM`, cria
backup consistente, chama o instalador versionado, valida HTTP, banco e ADS e
mantém o rollback automático. Nenhum endpoint aceita comandos ou caminhos
arbitrários.

O token não fica no `appsettings`. No servidor, ele é gravado em arquivo com ACL
restrita a `SYSTEM` e Administradores pelo script:

```powershell
.\Set-PaperMachineHistorianUpdateToken.ps1
```

Use um fine-grained personal access token com acesso somente ao repositório e
permissão **Contents: Read**. Consulte [DEPLOYMENT.md](DEPLOYMENT.md) para o
fluxo completo de publicação da Release e instalação inicial do atualizador.

## API inicial

- `GET /health`;
- `GET /api/runtime`;
- `GET /api/current`;
- `GET /api/history/status`;
- `GET /api/history/status-changes`;
- `GET /api/history/motors`;
- `GET /api/history/productivity`;
- `GET /api/history/commands`;
- `GET /api/history/alarms`;
- `GET /api/history/breaks`;
- `GET /api/reports/production-breaks`;
- `GET /api/storage`;
- `GET /api/updates/status`;
- `POST /api/updates/check`;
- `POST /api/updates/download`;
- `POST /api/updates/install`.

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
O relatório PDF aceita `start`, `end` e `productiveSpeedMpm`, exige autenticação
e respeita os limites de período e quantidade configurados em `Reporting`.
Ele apresenta resumo operacional, produtividade e quebras por hora, Pareto das
causas e o detalhamento das quebras. Eventos ainda não analisados aparecem como
**Causa pendente de análise**.
A análise de correlação relaciona cada quebra a comandos e mudanças discretas
de estado ocorridos entre cinco minutos antes e um minuto depois do seu início.
Ela destaca recorrência temporal, mas não atribui causalidade automaticamente.
As APIs do Historian exigem autenticação por cookie; `/health` e o fluxo inicial de autenticação permanecem públicos.
As APIs de atualização exigem o perfil `Administrator`. A instalação exige que
a versão confirmada corresponda exatamente ao pacote validado e preparado.

## Limites desta versão

- não escreve comandos no PLC;
- mudança em `paperMachineHmiCommands` é capturada por notificação ADS imediata,
  mas significa **comando observado**, não confirmação de execução;
- a telemetria histórica padrão é gravada a cada 5 segundos e não representa
  picos analógicos mais rápidos; o torque exato de falha continua retido no PLC;
- correlações de quebra são indícios temporais e precisam de confirmação por
  alarme, diagnóstico do drive ou análise técnica;
- campos sem unidade inequívoca no DUT são exibidos como `unidade PLC`;
- a primeira entrega de relatórios cobre produção e quebras; relatórios
  individuais de diagnóstico, alarmes e comandos ficam para as próximas etapas.
