# Compatibilidade multiplataforma

Compatibilidade entre sistemas operacionais é requisito arquitetural do
Historian e de novos componentes. Código dependente de plataforma deve ficar em
adaptadores isolados; formato de dados, regras, configuração e protocolos devem
permanecer portáveis.

## Matriz desta entrega

| Componente | Windows x64 | Linux x64 | Linux ARM64 |
| --- | --- | --- | --- |
| Conector ERP | homologado e executado | compilação cruzada validada | compilação cruzada validada |
| Serviço do conector | Windows Service | processo com SIGTERM; unit systemd pendente | processo com SIGTERM; unit systemd pendente |
| Historian completo | produção atual | não homologado por dependência ADS/TwinCAT | não homologado por dependência ADS/TwinCAT |

“Multiplataforma” não significa que o TwinCAT ADS Router atual foi homologado
em todos os alvos. Significa que o novo conector não depende do Schannel, .NET,
OpenSSL instalado, CGO ou DLL específica e que os binários Linux são gerados e
verificados a cada release aplicável.

## Toolchain reproduzível

- Go `1.26.5`;
- dependências fixadas por `go.mod` e `go.sum`;
- `CGO_ENABLED=0`;
- `-trimpath` e `-buildvcs=false`;
- versão injetada no binário por `-ldflags`.

No computador de desenvolvimento:

```powershell
$env:CPNTECK_GO_EXE = 'C:\caminho\go\bin\go.exe'
.\scripts\Build-ProductionConnector.ps1 -Version 0.3.0
```

O script executa `go test ./...` e gera:

```text
CPNTeck.ProductionConnector-windows-amd64.exe
CPNTeck.ProductionConnector-linux-amd64
CPNTeck.ProductionConnector-linux-arm64
```

O `Publish-PaperMachineHistorian.ps1` seleciona automaticamente o binário que
corresponde ao `RuntimeIdentifier`. Uma plataforma nova só entra na lista após
adaptador de serviço, build, testes e instruções de instalação próprios.

## Regras para próximas integrações

1. Não acessar APIs externas diretamente a partir de código preso ao runtime do
   sistema operacional quando um protocolo portátil for necessário.
2. Não incluir caminhos Windows nas regras de negócio; resolvê-los no adaptador
   de plataforma ou na configuração.
3. Não declarar suporte apenas por intenção: manter alvo de build e teste.
4. Não reduzir versão TLS, desabilitar certificado ou ativar redirecionamento
   para contornar incompatibilidade.
5. Documentar separadamente “build validado” e “ambiente homologado”.
