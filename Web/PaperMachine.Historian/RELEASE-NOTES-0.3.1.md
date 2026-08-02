## CPNTeck Paper Machine Historian 0.3.1

### Correções do atualizador

- corrige a criação do serviço `CPNTeckProductionConnector` no Windows 10 LTSC
  com Windows PowerShell 5.1;
- elimina a passagem de caminhos complexos com aspas para `sc.exe`;
- remove a pasta de uma release incompleta depois de um rollback bem-sucedido;
- arquiva a solicitação de instalação que falhou para impedir nova execução
  acidental;
- preserva e exibe separadamente a última falha de instalação, sem apagá-la em
  uma consulta posterior ao GitHub;
- aplica a mesma troca segura de caminho no rollback manual.

### Atualização do servidor

O servidor que permaneceu na versão 0.2.3 deve instalar diretamente a versão
0.3.1 pela página de atualizações. A pasta residual da versão 0.3.0 não deve ser
forçada nem utilizada como indicação de versão ativa.
