# Implantacao do CPNTeck Paper Machine Historian

O pacote de producao contem o backend ASP.NET, o frontend React compilado e o
runtime .NET para Windows x64. O computador de producao nao precisa de Node.js,
Visual Studio, IIS ou SDK .NET.

O fluxo completo do atualizador, sua avaliação de segurança e o checklist para
replicação estão em [UPDATE-SYSTEM.md](UPDATE-SYSTEM.md).

## 1. Gerar o pacote

No computador de desenvolvimento, abra o PowerShell na pasta
`Web\PaperMachine.Historian`:

```powershell
.\scripts\Publish-PaperMachineHistorian.ps1 -Version 0.1.3
```

O arquivo final sera criado em:

```text
artifacts\CPNTeck-PaperMachineHistorian-0.1.3-win-x64.zip
artifacts\CPNTeck-PaperMachineHistorian-0.1.3-win-x64.zip.sha256
```

O build executa os testes, compila o frontend, publica uma aplicacao
autocontida, gera o manifesto interno e cria o arquivo SHA-256 externo usado
pela atualizacao automatica.

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
- instala a tarefa `CPNTeckPaperMachineHistorianUpdater` como `SYSTEM`;
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

Na primeira atualizacao com armazenamento otimizado, o instalador adiciona as
novas opcoes de telemetria e retencao sem substituir AMS Net ID, IP, banco,
usuarios ou outros valores personalizados. O intervalo legado de snapshot de
10 segundos e migrado para 60 segundos. O schema SQLite e aditivo e a conversao
do historico antigo ocorre progressivamente em lotes pequenos.

Para reinstalar exatamente a mesma versao, acrescente `-Force`.

## 6. Configurar atualizacao pelo repositorio privado

A primeira passagem de uma versao antiga para a versao que introduz este fluxo
deve ser feita uma vez pelo procedimento manual da secao 5. Essa instalacao
cria a tarefa externa e a estrutura `updates`. Depois disso, as releases
seguintes poderao ser verificadas, baixadas e instaladas pela pagina.

### 6.1 Criar o token

No GitHub, crie um **fine-grained personal access token**:

1. limite o token ao repositorio `FabioTCampion/ADF_MP1_TC4024_67`;
2. conceda somente `Repository permissions > Contents > Read-only`;
3. defina prazo de expiracao e registre sua renovacao;
4. nao conceda permissao de escrita, administracao ou workflows.

No servidor, dentro da pasta extraida do pacote, execute como Administrador:

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
  .\Set-PaperMachineHistorianUpdateToken.ps1
```

O prompt mascara o token. Ele e gravado em:

```text
C:\ProgramData\CPNTeck\PaperMachineHistorian\updates\github-token.txt
```

Somente `SYSTEM` e Administradores recebem acesso ao arquivo. O token nunca e
retornado pela API, pela pagina ou pelos logs.

### 6.2 Publicar uma Release

Uma atualizacao so e descoberta quando existe uma Release estavel com:

- tag `v<versao>` ou `<versao>`, por exemplo `v0.1.3`;
- ZIP com nome exato
  `CPNTeck-PaperMachineHistorian-<versao>-win-x64.zip`;
- checksum com o mesmo nome acrescido de `.sha256`.

Envie os dois arquivos gerados em `artifacts` como assets da Release. Releases
marcadas como draft ou pre-release nao sao retornadas pelo canal estavel.
Sempre publique a partir de commit/tag limpo; `GitDirty: true` no manifesto
indica pacote sem rastreabilidade completa.

### 6.3 Operacao pela pagina

O servidor consulta o GitHub a cada 30 minutos e baixa o pacote novo em segundo
plano. Um Administrador pode abrir **Atualizacoes** para:

- consultar a versao instalada e disponivel;
- verificar o GitHub imediatamente;
- acompanhar download e SHA-256;
- ler as notas da Release;
- confirmar a instalacao digitando a versao.

Ao confirmar, a tarefa externa:

1. valida novamente caminho e SHA-256;
2. para somente o servico do Historian;
3. cria backup do banco, WAL/SHM existentes, configuracao e chaves;
4. instala a nova release;
5. inicia e valida servidor, frontend, banco e ADS;
6. restaura o executavel anterior se a validacao falhar.

O TwinCAT e o PLC nao sao reiniciados. Existe apenas um pequeno intervalo sem
coleta do Historian durante a troca. No maximo cinco backups automaticos
`before-*` sao mantidos.

Pastas operacionais:

```text
C:\ProgramData\CPNTeck\PaperMachineHistorian\updates\pending
C:\ProgramData\CPNTeck\PaperMachineHistorian\updates\installed
C:\ProgramData\CPNTeck\PaperMachineHistorian\updates\failed
C:\ProgramData\CPNTeck\PaperMachineHistorian\updates\logs
```

Verificacao manual da tarefa:

```powershell
Get-ScheduledTask CPNTeckPaperMachineHistorianUpdater
Get-Content `
  C:\ProgramData\CPNTeck\PaperMachineHistorian\updates\update-status.json
```

## 7. Rollback manual

```powershell
powershell.exe -ExecutionPolicy Bypass -File `
  .\Rollback-PaperMachineHistorianService.ps1
```

O rollback troca o executavel para a release anterior e preserva os dados.

## 8. Diagnostico

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

## 9. Banco existente

O procedimento padrao cria um banco novo no servidor. Para migrar o historico
de outro computador, pare a aplicacao de origem e o servico de destino antes de
copiar `PaperMachineHistorian.db`. Nao copie arquivos `-wal` ou `-shm` com a
aplicacao em execucao.
