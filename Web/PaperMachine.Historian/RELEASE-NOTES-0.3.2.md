## CPNTeck Paper Machine Historian 0.3.2

### Correção do conector no Windows 10 LTSC

- faz o modo explícito `--service` conectar-se imediatamente ao Service Control
  Manager;
- evita executar `svc.IsWindowsService()` antes do registro quando o processo já
  foi iniciado pela tarefa de instalação como serviço;
- corrige o timeout `7009` observado no Windows 10 Enterprise LTSC 1809;
- adiciona testes de regressão para execução forçada como serviço, detecção
  automática e modo console;
- mantém todas as proteções de backup, rollback e validação introduzidas na
  versão 0.3.1.

### Atualização do servidor

O servidor que retornou automaticamente à versão 0.2.3 deve instalar diretamente
a versão 0.3.2. Não reinstale as versões 0.3.0 ou 0.3.1 e não use `-Force`.
