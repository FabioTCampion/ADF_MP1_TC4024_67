# Integração de contexto de produção ERP

## Objetivo e limites

O Historian consulta o PaperSystem a cada 60 segundos, independentemente do
coletor ADS. A integração é somente leitura: não altera ERP nem PLC. Nesta
entrega, o painel mostra OP, mapa, qualidade, clientes, pedidos e jumbos. O
histórico dedicado e a correlação estatística entre qualidade, processo e
períodos sem quebra continuam previstos pelo schema, mas ficam para a próxima
entrega.

## Arquitetura TLS multiplataforma

O endpoint atual do PaperSystem exige TLS 1.3. Como o Schannel do Windows 10
Enterprise LTSC 1809 da CPU industrial não negocia esse protocolo, a conexão
externa foi isolada em um executável pequeno escrito em Go:

```text
ProductionIntegrationWorker
  -> HTTP + token, somente 127.0.0.1:5091
CPNTeckProductionConnector
  -> HTTPS TLS 1.3 + x-api-key
api.papersystem.com.br
```

O Historian é impedido por validação de configuração de apontar para um host
remoto. O conector:

- escuta apenas em IP numérico de loopback;
- aceita somente o host externo fixado na configuração;
- exige TLS 1.3 e valida a cadeia/correspondência do certificado;
- não usa proxy do sistema e não segue redirecionamentos;
- limita a resposta a 1 MiB e valida os campos essenciais do JSON;
- exige um token local próprio, diferente da chave ERP;
- nunca registra chave, token ou corpo completo da resposta;
- mantém cache de 55 segundos para evitar consultas externas duplicadas.

O código não usa CGO nem DLL nativa. O mesmo fonte gera binários para Windows
x64, Linux x64 e Linux ARM64. Consulte [MULTIPLATFORM.md](MULTIPLATFORM.md) para
a matriz de suporte e os comandos reproduzíveis.

## Serviços e segredos no Windows

O instalador cria dois serviços com início automático atrasado:

- `CPNTeckProductionConnector`;
- `CPNTeckPaperMachineHistorian`, dependente do conector.

Arquivos locais:

```text
C:\ProgramData\CPNTeck\ProductionConnector\config.json
C:\ProgramData\CPNTeck\ProductionConnector\secrets\erp-api-key.txt
C:\ProgramData\CPNTeck\ProductionConnector\secrets\historian-token.txt
```

O `appsettings.Production.json` contém somente o caminho do token local. A chave
ERP fica exclusivamente no diretório do conector. As ACLs permitem acesso a
`SYSTEM` e `Administrators`; chave e token não entram no Git, ZIP ou logs.

## Validar a chave atual sem substituí-la

Instale primeiro a release, pois ela cria o serviço, a configuração e o token
local. Em PowerShell como Administrador, execute:

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
  .\Test-PaperMachineHistorianErpConnection.ps1
```

O script solicita a chave de modo protegido, cria um arquivo temporário com ACL
restrita, executa uma consulta TLS 1.3 e apaga o temporário. Ele mostra somente
mapa, OP, estado de produção e quantidade de itens. A chave instalada e as
configurações permanecem inalteradas.

## Instalar ou trocar a chave

Depois que a validação anterior passar:

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
  .\Set-PaperMachineHistorianErpApiKey.ps1
```

O script valida a chave antes da troca. Somente após sucesso ele faz a
substituição atômica, habilita `ProductionIntegration` com o endpoint local e
reinicia conector e Historian. Se TLS, autenticação ou JSON falharem, a chave
anterior é preservada.

Uma chave exposta em captura, terminal compartilhado ou canal externo deve ser
revogada no ERP. O fato de ser somente leitura reduz impacto, mas não elimina o
risco de acesso a dados de produção.

## Isolamento operacional

- `HistorianWorker` continua responsável somente pelo ADS;
- `ProductionIntegrationWorker` consulta o conector separadamente;
- falha ERP nunca interrompe coleta ADS nem é convertida em parada;
- um mapa termina apenas com `produzindo = false` ou novo identificador;
- após 180 segundos sem sucesso, o painel marca os dados como desatualizados;
- falhas consecutivas usam espera de 1, 2, 4 e no máximo 5 minutos.

## Modelo genérico e schema 10

O contrato interno usa nomes independentes do fornecedor. O adaptador atual é
`PaperSystemProductionSourceClient`, atrás de `IProductionSourceClient`.

O schema 10 contém `ExternalProductionRuns`, `ExternalProductionRunItems`,
`ExternalProductionReferences`, `ExternalProductionSnapshots`,
`ProductionQualityPeriods` e `IntegrationSyncState`. O JSON bruto só é gravado
quando muda. O formato atual é a soma dos três primeiros formatos positivos do
mapa. A receita é `ProductCode + GrammageGsm + ProductionWidthMm`.

Os itens representam o destino dos jumbos depois da rebobinadeira; não
representam produção simultânea de qualidades diferentes na máquina de papel.
Uma divergência de produto ou gramatura é mantida como sinal de conferência da
origem, sem presumir uma qualidade arbitrária. O painel combina formato e
gramatura do ERP com a velocidade ADS e mostra a taxa teórica instantânea
somente quando o ERP está atualizado, a máquina está produzindo e há papel.

## Backup, atualização e rollback

O conector está dentro da mesma release e do mesmo manifesto SHA-256 do
Historian. Antes da atualização, o instalador para os dois serviços e guarda
banco, WAL/SHM, configuração, chaves de sessão e configuração/segredos do
conector. No rollback, ambos os binários e seus dados correspondentes voltam em
conjunto. Não existe fallback silencioso para OpenSSL ou para TLS inseguro.

## Diagnóstico

```powershell
Get-Service CPNTeckProductionConnector, CPNTeckPaperMachineHistorian
Invoke-RestMethod http://127.0.0.1:5091/health
```

O endpoint de produção local exige o cabeçalho com o token e não deve ser
consultado manualmente exibindo o segredo no terminal. Para diagnóstico de
autenticação, use sempre o script de validação.
