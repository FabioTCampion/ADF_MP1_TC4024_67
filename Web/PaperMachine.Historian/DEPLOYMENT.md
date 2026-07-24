# Implantacao do CPNTeck Paper Machine Historian

O pacote de producao contem o backend ASP.NET, o frontend React compilado e o
runtime .NET para Windows x64. O computador de producao nao precisa de Node.js,
Visual Studio, IIS ou SDK .NET.

## 1. Gerar o pacote

No computador de desenvolvimento, abra o PowerShell na pasta
`Web\PaperMachine.Historian`:

```powershell
.\scripts\Publish-PaperMachineHistorian.ps1 -Version 0.1.0
```

O arquivo final sera criado em:

```text
artifacts\CPNTeck-PaperMachineHistorian-0.1.0-win-x64.zip
```

O build executa os testes, compila o frontend, publica uma aplicacao
autocontida e gera um manifesto SHA-256 de todos os arquivos do pacote.

## 2. Preparar o computador servidor

Antes da primeira instalacao:

1. use Windows x64;
2. instale e inicie o TwinCAT ADS Router;
3. configure no servidor a rota ADS para o PLC:
   - AMS Net ID: `192.168.100.1.1.1`;
   - IP remoto: `10.8.0.4`;
   - porta ADS do runtime PLC: `851`;
4. confirme que o servidor alcanca `10.8.0.4` pela rede/VPN;
5. copie o ZIP para uma pasta temporaria local;
6. extraia o ZIP;
7. abra o PowerShell como Administrador dentro da pasta extraida.

O servico escuta em `http://0.0.0.0:5088`. O instalador cria uma regra no
Firewall do Windows somente para os perfis `Privado` e `Dominio`, limitada a
`LocalSubnet`. A porta nao deve ser exposta diretamente na Internet.

## 3. Primeira instalacao

Execute como Administrador:

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
  .\Install-PaperMachineHistorianService.ps1 `
  -AmsNetId '192.168.100.1.1.1' `
  -RemoteIp '10.8.0.4'
```

O instalador:

- valida o SHA-256 dos arquivos;
- copia a versao para
  `C:\Program Files\CPNTeck\PaperMachineHistorian\releases\<versao>`;
- cria a configuracao e o banco em
  `C:\ProgramData\CPNTeck\PaperMachineHistorian`;
- instala o servico `CPNTeckPaperMachineHistorian`;
- configura o inicio como **automatico atrasado**;
- configura tres tentativas de reinicio automatico em caso de falha;
- abre a porta 5088 somente para a rede local;
- valida servidor, frontend, banco SQLite e comunicacao ADS.

O banco de producao e criado vazio na primeira execucao. No primeiro acesso, a
aplicacao solicita a criacao do usuario administrador.

Depois da instalacao, abra:

```text
http://127.0.0.1:5088
http://<IP-OU-NOME-DO-SERVIDOR>:5088
```

## 4. Validar a instalacao

```powershell
.\Test-PaperMachineHistorianInstallation.ps1
```

Verificacoes manuais:

```powershell
Get-Service CPNTeckPaperMachineHistorian
Invoke-RestMethod http://127.0.0.1:5088/api/version
Invoke-RestMethod http://127.0.0.1:5088/health/ready
Invoke-WebRequest http://127.0.0.1:5088/ -UseBasicParsing |
  Select-Object StatusCode, ContentType, RawContentLength
```

Os resultados esperados sao:

- servico `Running`;
- inicio `Automatic`;
- `ready: true`;
- `databaseAvailable: true`;
- `plcOnline: true`;
- pagina principal HTTP 200.

O Windows apresenta servicos configurados como automaticos atrasados com
`StartType = Automatic`.

## 5. Atualizar

Copie e extraia o novo ZIP e execute como Administrador:

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
  .\Update-PaperMachineHistorianService.ps1
```

A atualizacao preserva banco, usuarios, chaves e configuracao em `ProgramData`.
O servico e parado somente durante a troca da release. Se a nova versao nao
passar nas verificacoes, o instalador restaura automaticamente o executavel
anterior.

Para reinstalar exatamente a mesma versao, acrescente `-Force`.

## 6. Rollback manual

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
  .\Rollback-PaperMachineHistorianService.ps1
```

O rollback troca o executavel para a release anterior e preserva os dados.

## 7. Diagnostico

```powershell
Get-Service CPNTeckPaperMachineHistorian
Get-NetTCPConnection -LocalPort 5088 -State Listen
Invoke-WebRequest http://127.0.0.1:5088/health -UseBasicParsing
Invoke-RestMethod http://127.0.0.1:5088/health/ready
```

Se o servidor HTTP estiver online, mas `plcOnline` estiver falso:

1. confirme que o TwinCAT ADS Router esta iniciado;
2. confira a rota ADS para `192.168.100.1.1.1`;
3. confirme acesso ao IP `10.8.0.4`;
4. confira a porta PLC `851`;
5. reinicie o servico:

```powershell
Restart-Service CPNTeckPaperMachineHistorian
```

A configuracao fica em:

```text
C:\ProgramData\CPNTeck\PaperMachineHistorian\appsettings.Production.json
```

Edite-a somente como Administrador e reinicie o servico depois de qualquer
alteracao.

## 8. Banco existente

O procedimento padrao cria um banco novo no servidor. Para migrar o historico
de outro computador, pare a aplicacao de origem e o servico de destino antes de
copiar `PaperMachineHistorian.db`. Nao copie arquivos `-wal` ou `-shm` com a
aplicacao em execucao.
