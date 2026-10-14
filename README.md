<p align="center">
  <img src="https://img.shields.io/badge/AVEVA-Process%20Simulation-7B2CBF?style=for-the-badge" alt="AVEVA Process Simulation"/>
  <img src="https://img.shields.io/badge/C%23-EETK-239120?style=for-the-badge&logo=csharp&logoColor=white" alt="C# EETK"/>
  <img src="https://img.shields.io/badge/Python-3.12+-3776AB?style=for-the-badge&logo=python&logoColor=white" alt="Python"/>
  <img src="https://img.shields.io/badge/Plataforma-Windows-0078D6?style=for-the-badge" alt="Windows"/>
</p>

<h1 align="center">🔗 AVEVA Process Simulation ⇄ Python<br>Ponte por equações externas (EETK)</h1>

<p align="center">
  <strong>Troca de dados entre o simulador e Python a cada passo de integração</strong><br>
  <em>Exemplo: sistema de quatro tanques.</em>
</p>

<p align="center">
  <a href="#visao-geral">🎯 Visão geral</a> •
  <a href="#versoes">🧩 Versões</a> •
  <a href="#inicio-rapido">🚀 Início rápido</a> •
  <a href="#protocolo">📡 Protocolo</a> •
  <a href="#validacao">🔬 Validação</a>
</p>

---

<a name="visao-geral"></a>

## 🎯 Visão geral

O AVEVA Process Simulation permite acrescentar equações a um modelo por meio de uma biblioteca
.NET (*External Equation Toolkit*, EETK). Este repositório usa esse recurso como **ponte de dados**:
a DLL é chamada pelo resolvedor a cada avaliação das equações e troca valores com um programa
Python por socket local. Assim é possível aplicar sequências de entradas e registrar a resposta do
modelo a partir de scripts, sem intervenção manual na interface.

---

<a name="versoes"></a>

## 🧩 Versões

| Pasta | Transporte | Situação |
|---|---|---|
| [`v1_udp/`](v1_udp/) | UDP | Versão original, mantida como histórico |
| [`v2_tcp/`](v2_tcp/) | TCP 127.0.0.1:5006 | Versão atual (substituição direta da v1: mesmo *namespace* e mesmas variáveis) |

### O que mudou da v1 para a v2

| v1 (UDP) | v2 (TCP) |
|---|---|
| Envia um pacote em **cada avaliação do resíduo** (várias iterações de Newton por passo): chegam valores não convergidos e duplicados | Envia **uma amostra por passo aceito**, com o valor convergido |
| O comando é aplicado quando chega, no meio de um passo | É aplicado na **fronteira de um passo** |
| Sem confirmação | `queued` (recebido) + `ack` (aplicado, com tempo e motivo) |
| 4 casas decimais | Precisão completa |
| Sem controle de perdas | TCP + número de sequência; fila sem bloqueio (o resolvedor nunca espera o Python) |
| — | Detecta passos rejeitados e restaurações de *snapshot* (`rewind`) |

---

<a name="inicio-rapido"></a>

## 🚀 Início rápido

### Requisitos

| Componente | Observação |
|---|---|
| AVEVA Process Simulation | Com o External Equation Toolkit |
| Visual Studio 2019 ou MSBuild | .NET Framework, para compilar a DLL |
| `SimCentral.Server.ExternalEquation.Interfaces.dll` | Distribuída com o EETK da AVEVA; **não está incluída aqui** (copiar para `Dependencies\`, na raiz do repositório) |
| Python 3.12+ | Apenas biblioteca padrão |

### 1 Compilar e instalar a DLL

```powershell
msbuild v2_tcp\tanques4.sln /p:Configuration=Release
```

Com o AVEVA **fechado**, copie `v2_tcp\bin\Release\tanques4.dll` para
`C:\Program Files\SimSci\SimCentral\SimCentral Addons\`. Guarde a DLL anterior **fora** dessa
pasta (se ficar dentro, o AVEVA pode carregar as duas). Detalhes em
[`v2_tcp/LEEME.md`](v2_tcp/LEEME.md).

### 2 Testar sem o AVEVA

[`v2_tcp/test/`](v2_tcp/test/) traz um resolvedor falso em C# (`FakeSolver.cs`) que carrega a DLL
real e a chama como o simulador: três iterações de Newton por passo, um passo rejeitado e uma
restauração de *snapshot*. `expected.csv` contém a trajetória sintética desse resolvedor.

```powershell
cd v2_tcp\test
python test_offline.py 200
```

O teste fora de linha só funciona com o AVEVA fechado (a porta TCP fica ocupada pela DLL carregada).

### 3 Usar a partir do Python

```python
from aveva_bridge_v2 import BridgeV2         # v2_tcp/python

br = BridgeV2(port=5006).connect()
print(br.ping())                             # tempo de ida e volta, em segundos
br.set_inputs(q1=0.08, q2=0.07)              # aplicado na próxima fronteira de passo (ack)
amostras = br.drain()                        # amostras recebidas até agora
br.close()
```

Observações práticas sobre a interface de programação do simulador:

- chame `OpenSimulation(...).Result` em cada sessão (a interface gráfica aberta não basta);
- use sempre `.Result` em `RunDynamics`; sem isso a chamada é assíncrona e os dados se perdem;
- conecte a ponte **depois** de restaurar o *snapshot*: a primeira restauração recarrega a DLL;
- `SolveSteadyStateDynamics` **não** reavalia as equações externas; estados estacionários devem ser
  obtidos por integração até a acomodação.

---

<a name="protocolo"></a>

## 📡 Protocolo

Mensagens JSON, uma por linha, sobre TCP local.

| Direção | Mensagens |
|---|---|
| Python → DLL | `{"cmd":"set","id":7,"q1":0.08,"q2":0.06,"apply":"step"}`, `flush`, `ping`, `status`, `reset_seq` |
| DLL → Python | `hello`, `queued`, `ack`, `sample`, `rewind`, `superseded`, `flushed`, `pong`, `status`, `error` |

Cada `sample` traz o tempo simulado, o número de sequência, as entradas aplicadas, as saídas e o
número de avaliações do resíduo no passo. Formato completo em
[`v2_tcp/EquationSet.cs`](v2_tcp/EquationSet.cs).

---

## 📂 Arquivos

| Arquivo | Descrição |
|---|---|
| `v2_tcp/Bridge.cs` | Servidor TCP, fila sem bloqueio e protocolo |
| `v2_tcp/EquationSet.cs` | Equações expostas ao resolvedor; uma amostra por passo aceito |
| `v2_tcp/Main.cs`, `v2_tcp/EquationSetType.cs` | Registro da biblioteca no EETK |
| `v2_tcp/python/aveva_bridge_v2.py` | Cliente Python |
| `v2_tcp/test/` | Resolvedor falso e teste fora de linha |
| `v1_udp/` | Versão original por UDP |
| [`docs/`](docs/) | Guia de integração EETK–Python (PDF) |

---

<a name="validacao"></a>

## 🔬 Validação da ponte

| Teste | Resultado |
|---|---|
| Fora de linha (resolvedor falso) | Todas as amostras coincidem com os valores convergidos (1e-12); 0 perdidas; ~75 000–85 000 amostras/s |
| No AVEVA | ping de 0,12 ms; 20 s simulados → 200 amostras (passo de 0,1 s) sem lacunas; comando confirmado na fronteira do passo |

---

## 👤 Autoria

**Ezequiel José Valencia Urbina** — Universidade de Brasília
📧 [urbina.ezequiel@aluno.unb.br](mailto:urbina.ezequiel@aluno.unb.br)
