# Build e Release otimizados

Este documento descreve o fluxo de build e publicação do CPNTeck Paper Machine
Historian. O objetivo é reduzir o tempo da Release sem enfraquecer as garantias
do atualizador instalado no servidor industrial.

## 1. Comando recomendado

Execute na pasta `Web\PaperMachine.Historian`, depois de revisar e commitar as
alterações da aplicação:

```powershell
.\scripts\Release-PaperMachineHistorian.ps1
```

Quando `-Version` não é informado, o script consulta a última Release estável,
incrementa o patch e mostra a versão escolhida. Por exemplo, depois de `v0.1.7`,
a versão automática será `0.1.8`.

Para escolher a versão explicitamente:

```powershell
.\scripts\Release-PaperMachineHistorian.ps1 -Version 0.1.8
```

O comando:

1. exige que a pasta da aplicação esteja limpa no Git;
2. valida a autenticação do GitHub CLI;
3. verifica se tag ou Release existentes podem ser retomadas com segurança;
4. compila, testa e gera o pacote;
5. confere manifesto e SHA-256;
6. cria uma tag anotada no commit exato do pacote;
7. envia branch e tag em uma única operação;
8. cria a Release como draft e envia os dois assets;
9. publica como stable/latest;
10. verifica tag, estado, nomes dos assets e digest remoto.

Alterações de PLC ou de outros projetos presentes no mesmo repositório não são
incluídas automaticamente. A condição de limpeza é aplicada à pasta
`Web\PaperMachine.Historian`.

## 2. Gerar somente o pacote

Para produzir ZIP e checksum sem alterar Git ou GitHub:

```powershell
.\scripts\Publish-PaperMachineHistorian.ps1 -Version 0.1.8
```

Esse é o comando indicado para validar localmente o empacotamento.

Os arquivos são criados em:

```text
artifacts\CPNTeck-PaperMachineHistorian-0.1.8-win-x64.zip
artifacts\CPNTeck-PaperMachineHistorian-0.1.8-win-x64.zip.sha256
```

## 3. O que foi otimizado

### 3.1 Dependências do frontend

O SHA-256 de `package-lock.json` é comparado com um marcador dentro de
`node_modules`. Quando o lockfile não mudou, o `npm ci` é dispensado.

Quando é necessário restaurar, o comando usado é:

```powershell
npm ci --prefer-offline --no-audit --fund=false
```

Isso mantém as versões travadas pelo lockfile e reutiliza o cache local do npm.
A auditoria não é repetida durante cada build. Ela deve continuar sendo
executada no processo periódico de manutenção das dependências.

Para forçar uma instalação completamente limpa:

```powershell
.\scripts\Publish-PaperMachineHistorian.ps1 `
  -Version 0.1.8 `
  -CleanClientDependencies
```

O comando de Release aceita o mesmo parâmetro.

### 3.2 Frontend compilado uma vez

O frontend passa por:

```text
lint -> TypeScript/Vite build -> wwwroot
```

Depois, o `dotnet publish` recebe `SkipClientBuild=true` e apenas empacota o
`wwwroot` já produzido. O alvo do projeto continua capaz de compilar o cliente
quando alguém executa `dotnet publish` diretamente.

### 3.3 Restore .NET único

O script executa um restore com o runtime solicitado e reutiliza o resultado:

```text
dotnet restore --runtime win-x64
dotnet test --no-restore
dotnet publish --no-restore
```

### 3.4 Testes sem parar o servidor local

Os binários dos testes são direcionados para a pasta temporária curta
`%TEMP%\CPNTeck\PaperMachineHistorianTests`. Assim, os testes não tentam
sobrescrever as DLLs que podem estar abertas pela instância local em
`http://localhost:5088` e não atingem o limite de comprimento de caminhos do
Windows.

A pasta temporária é validada e removida depois da execução.

### 3.5 Compactação rápida

O pacote continua sendo ZIP e permanece compatível com `Expand-Archive`, mas a
compactação padrão passou de `Optimal` para `Fastest`.

Essa escolha reduz o uso de CPU e o tempo de empacotamento. O pacote pode ficar
um pouco maior, sem alterar conteúdo, manifesto ou SHA-256.

Para comparar ou produzir o menor ZIP possível:

```powershell
.\scripts\Publish-PaperMachineHistorian.ps1 `
  -Version 0.1.8 `
  -CompressionLevel Optimal
```

## 4. Parâmetros operacionais

### `Publish-PaperMachineHistorian.ps1`

| Parâmetro | Finalidade |
| --- | --- |
| `-Version` | Versão SemVer gravada no binário e manifesto |
| `-RuntimeIdentifier` | Runtime, padrão `win-x64` |
| `-CleanClientDependencies` | Força novo `npm ci` |
| `-SkipClientBuild` | Reutiliza o `wwwroot`; somente para diagnóstico |
| `-SkipTests` | Pula testes; não usar em Release oficial |
| `-CompressionLevel` | `Fastest`, `Optimal` ou `NoCompression` |

### `Release-PaperMachineHistorian.ps1`

| Parâmetro | Finalidade |
| --- | --- |
| `-Version` | Opcional; sem ele, incrementa o patch automaticamente |
| `-Repository` | Repositório `owner/name` |
| `-GitHubCliPath` | Caminho opcional para `gh.exe`; há detecção automática no Windows |
| `-RuntimeIdentifier` | Runtime, padrão `win-x64` |
| `-ReleaseNotesPath` | Arquivo Markdown com notas da versão |
| `-CleanClientDependencies` | Força restauração limpa do cliente |
| `-DraftOnly` | Envia e valida os assets, mas não publica o draft |
| `-SkipTests` | Diagnóstico apenas; não usar oficialmente |
| `-CompressionLevel` | Nível de compactação do ZIP |

Exemplo com notas próprias e parada em draft:

```powershell
.\scripts\Release-PaperMachineHistorian.ps1 `
  -Version 0.1.8 `
  -ReleaseNotesPath .\release-notes-0.1.8.md `
  -DraftOnly
```

Depois de revisar o draft, execute novamente o mesmo comando sem `-DraftOnly`.
O script reconhece a tag e o draft, substitui os assets de forma controlada e
conclui a publicação.

## 5. Saída e diagnóstico de desempenho

O empacotador mostra uma tabela com o tempo de cada etapa:

```text
Restore client dependencies
Lint client
Build client once
Restore .NET dependencies once
Run tests without locking the local server
Publish self-contained application
Hash package files
Create ZIP (Fastest)
```

Essa medição permite identificar se a demora está no npm, compilador, testes,
publicação, compactação ou rede, em vez de repetir todo o fluxo sem diagnóstico.

## 6. Retomada depois de falha

O processo foi projetado para ser retomado:

- tag local existente é aceita somente quando aponta para o `HEAD`;
- tag remota existente é aceita somente quando aponta para o mesmo commit;
- draft existente pode receber novamente ZIP e checksum;
- Release estável existente nunca é sobrescrita;
- tag apontando para outro commit interrompe o processo;
- alterações geradas pelo build interrompem a publicação antes da tag.

Se a conexão cair durante o upload, execute novamente exatamente o mesmo
comando. Não apague nem mova a tag.

## 7. Garantias que não foram removidas

A otimização não altera o contrato consumido pelo servidor:

- pacote autocontido para Windows x64;
- manifesto interno com versão, commit e estado do Git;
- SHA-256 de cada arquivo do pacote;
- SHA-256 externo do ZIP;
- Release criada inicialmente como draft;
- dois assets com nomes determinísticos;
- publicação stable/latest;
- tag imutável;
- atualizador do servidor continua validando versão e checksum antes de instalar.

## 8. Próxima evolução: GitHub Actions

O próximo passo opcional é executar o mesmo empacotador em um runner Windows do
GitHub Actions com disparo manual e aprovação de ambiente. Isso retira o custo
de compilação da estação de engenharia e torna o ambiente de build descartável.

Essa automação não faz parte desta primeira otimização. Antes de habilitá-la,
devem ser definidos ruleset de tags, permissões mínimas `contents: write`,
aprovação do ambiente de Release e política de retenção de artefatos.
