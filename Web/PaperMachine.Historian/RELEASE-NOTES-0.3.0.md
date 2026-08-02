## CPNTeck Paper Machine Historian 0.3.0

### Principais alterações

- integração somente leitura com o PaperSystem, consultada a cada 60 segundos;
- schema 9 com mapas de produção, OPs, itens, jumbos, snapshots por mudança e períodos de qualidade;
- card de contexto de produção ERP na página principal;
- conector local TLS 1.3 independente do Schannel do Windows 10 LTSC 1809;
- novo serviço `CPNTeckProductionConnector`, restrito a `127.0.0.1:5091`;
- build do conector validado para Windows x64, Linux x64 e Linux ARM64;
- backup e rollback conjunto do Historian, banco, configurações e conector;
- scripts seguros para validar, instalar e trocar a chave ERP sem gravá-la no Git ou no JSON.

### Atualização

A release pode ser instalada sem a chave ERP e mantém a integração desabilitada.
Depois da atualização, valide a chave atual com
`Test-PaperMachineHistorianErpConnection.ps1`. Somente após o teste, execute
`Set-PaperMachineHistorianErpApiKey.ps1` para instalar a chave e habilitar a
integração.

O instalador verifica o manifesto e o SHA-256, cria backup antes da migração e
valida os serviços, frontend, banco e comunicação ADS.
