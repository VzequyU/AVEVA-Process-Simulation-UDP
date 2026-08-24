<p align="center">
  <img src="https://img.shields.io/badge/AVEVA-Process%20Simulation-purple?style=for-the-badge&logo=data:image/png;base64," alt="AVEVA"/>
  <img src="https://img.shields.io/badge/Python-3.8+-blue?style=for-the-badge&logo=python&logoColor=white" alt="Python"/>
  <img src="https://img.shields.io/badge/C%23-.NET%20DLL-green?style=for-the-badge&logo=csharp&logoColor=white" alt="C#"/>
  <img src="https://img.shields.io/badge/Protocol-UDP%20Sockets-orange?style=for-the-badge" alt="UDP"/>
</p>

<h1 align="center">🔗 AVEVA Process Simulation ↔ Python<br>Real-Time UDP Bridge</h1>

<p align="center">
  <strong>Bidirectional real-time communication between AVEVA Process Simulation and Python</strong><br>
  <em>Control valves, read sensors, build soft sensors, and create digital twins — all from Python.</em>
</p>

<p align="center">
  <a href="https://youtu.be/r88qSzDYIMg">📺 Video Tutorial (Portuguese)</a> •
  <a href="#-quick-start">🚀 Quick Start</a> •
  <a href="#-documentation">📖 Documentation</a> •
  <a href="#-examples">💡 Examples</a>
</p>

---

## 🎯 What Is This?

This project enables **real-time bidirectional communication** between [AVEVA Process Simulation](https://www.aveva.com/en/products/process-simulation/) and Python using UDP sockets. It bridges the gap between rigorous equation-oriented (EO) process simulation and modern data science tools.

```
┌─────────────┐     UDP (JSON)     ┌─────────────┐     Residuals     ┌──────────────────┐
│   Python     │◄──────────────────►│   C# DLL    │◄────────────────►│  AVEVA Process   │
│  Controller  │   Port 5005/5006   │   (EETK)    │    EO Solver     │   Simulation     │
│  ML / Plot   │                    │EquationSet  │                  │  (Dynamics)      │
└─────────────┘                    └─────────────┘                  └──────────────────┘
```

### Key Features

- ✅ **Send commands** to AVEVA from Python (valve openings, flow setpoints)
- ✅ **Read process data** in real time (flows, levels, temperatures, pressures)
- ✅ **Non-blocking UDP** — does not slow down the EO solver
- ✅ **Plug-and-play** — pre-configured simulation files included
- ✅ **Extensible** — ready for soft sensors, MPC, ML inference, PI System integration

---

## 🏗️ Architecture

The system works by injecting a C# DLL into AVEVA's External Equation Toolkit (EETK). The DLL communicates with Python via UDP sockets while simultaneously providing residual equations to AVEVA's solver.

```
Python (valve.py)                    C# DLL (EquationSet.cs)              AVEVA
──────────────────                   ─────────────────────────            ──────
                                     
 1. User types: 0.75  ──UDP:5006──►  Receives targetPos = 0.75
                                     Computes residual:
                                       ManPos - 0.75 = 0         ──────►  Solver converges
                                                                          ManPos → 0.75
                                     Reads v[FlujoSink] from AVEVA
 Displays flow data  ◄──UDP:5005──  Sends JSON telemetry
```

### The Degrees of Freedom Principle

In an EO simulator, you cannot force a variable directly. Instead:

1. **Unspec** the variable in AVEVA (e.g., uncheck `XV1.ManPos`)
2. Your DLL provides a **new equation**: `residual = ManPos_AVEVA - Target_Python = 0`
3. The solver converges, making `ManPos` equal to your Python setpoint

---

## 🚀 Quick Start

### Prerequisites

| Software | Version | Purpose |
|----------|---------|---------|
| AVEVA Process Simulation | 2023+ | Process simulator |
| Visual Studio | 2019 / 2022 | C# DLL compilation |
| EETK | Included with AVEVA | SDK + templates |
| Python | 3.8+ | External controller |

### Step-by-Step

**1. Clone this repository**
```bash
git clone https://github.com/VzequyU/AVEVA-Process-Simulation-UDP.git
```

**2. Extract simulation files**

Extract `exemplos.rar` to get the pre-configured AVEVA simulation files and Visual Studio projects.

**3. Build the DLL**

Open the Visual Studio solution (e.g., `valvula/valvula.sln`) and build:
- `Build` → `Rebuild Solution` (x64, Release)
- Copy the resulting `.dll` to `SimCentral Addons\Engineering\`

**4. Configure AVEVA**

Open the included simulation file. The Model Type is pre-configured with:
- Variables mapped to the DLL
- External equations reference set
- Bridge equations connecting DLL variables to process equipment

**5. Run**

```bash
# Terminal 1: Start Python listener FIRST
python valve.py

# Then in AVEVA: Switch to Dynamics → Play
```

**6. Control the valve**
```
=== AVEVA <-> Python Control Panel ===
Enter a value between 0.0 and 1.0 (or 'q' to quit)
Valve position: 0.75
>>> Command 0.75 sent to AVEVA.
[AVEVA] Flow: 38.4938 kg/s | Valve: 0.75
```

---

## 💡 Examples

### Example 1: Single Valve Control

A simple `SRC → XV1 → SNK` flowsheet where Python controls the valve opening and reads the outlet mass flow.

| File | Description |
|------|-------------|
| `valvula/EquationSet.cs` | C# DLL with 1 equation, 2 variables (ManPos, FlujoSink) |
| `valve.py` | Python interactive console controller |

### Example 2: Johansson Quadruple-Tank System

A classic control benchmark with 4 interconnected tanks. Python controls two inlet flows (Q1, Q2) and monitors four liquid levels (H1–H4) with real-time matplotlib visualization.

| File | Description |
|------|-------------|
| `tanques4/EquationSet.cs` | C# DLL with 2 equations, 6 variables |
| `tanques4.py` | Python controller with real-time 4-subplot animation |

---

## 📖 Documentation

A comprehensive **16-page technical guide** (PDF) is included in this repository, covering:

- Complete EETK installation and setup
- C# code walkthrough with Jacobian derivation
- AVEVA Model Editor configuration (step-by-step with screenshots)
- Degrees of Freedom management
- Troubleshooting common errors (CS0246, singularity, DLL not found)
- Extension roadmap: soft sensors, MPC, PI System, green hydrogen

📄 **[Download the PDF Guide](./AVEVA_EETK_Python_Integration_Guide.pdf)**

---

## 🔧 Troubleshooting

<details>
<summary><strong>CS0246: Type "Severity" could not be found</strong></summary>

The project lost its reference to `SimCentral.Server.ExternalEquation.Interfaces`. In Solution Explorer, remove the broken reference and re-add it via Browse, pointing to the DLL in the EETK folder. Make sure the project is saved **inside the EETK directory**.
</details>

<details>
<summary><strong>AVEVA shows "DLL not found" or External status is red</strong></summary>

1. Verify the DLL is in `SimCentral Addons\Engineering\`
2. Check the Implementation Class matches `SimCentral.Examples.<project>.Main`
3. **Restart AVEVA** after copying the DLL (Windows locks DLLs in memory)
</details>

<details>
<summary><strong>Singularity / Red crosses after switching to External</strong></summary>

All variables in `VariableNames` must appear in the `Incidence` matrix, even if their derivative is 0.0. Add missing variables to each equation's incidence array.
</details>

<details>
<summary><strong>Python receives no data</strong></summary>

1. Start Python **before** the simulation
2. Verify ports: Python listens on 5005, C# sends to 5005
3. AVEVA must be in **Dynamics mode** and running (Play button)
4. Check no firewall is blocking localhost UDP
</details>

<details>
<summary><strong>Valve does not respond to commands</strong></summary>

The controlled variable (e.g., `XV1.ManPos`) must be **unspecified** (unchecked) in AVEVA. If it remains specified, AVEVA's own spec overrides the DLL equation.
</details>

---

## 🚀 Extending the System

This architecture is the foundation for advanced applications:

- **Soft Sensors** — Feed telemetry to ML models (scikit-learn, TensorFlow) for real-time inference of unmeasured variables
- **Model Predictive Control** — Replace simple setpoints with MPC optimization
- **AVEVA PI System** — Write telemetry to PI tags for historian storage
- **Green Hydrogen** — Apply to electrolyzer/fuel cell models for H₂ production optimization
- **Refinery Inference** — Deploy trained models for distillation column quality prediction

---

## 📺 Video Tutorial

🎬 **[Watch the installation tutorial on YouTube](https://youtu.be/r88qSzDYIMg)** (Portuguese)

English version coming soon.

---

## 👤 Author

**Ezequiel José Valencia Urbina**

- 🎓 M.Sc. Candidate — Mechanical Systems Dynamics, University of Brasília (UnB)
- 🔬 Research: Digital Twins, Soft Sensors, Process Control, Green Hydrogen
- 📧 urbina.ezequiel@aluno.unb.br

### Research Areas
- AVEVA Process Simulation & PI System integration
- Real-time inference in petroleum refineries (Petrobras collaboration)
- Equation-oriented simulation and advanced control
- Machine learning for process industry

---

## 📝 License

This project is open source. Feel free to use, modify, and distribute.

If this project helped you, please ⭐ **star this repository** and share it with your colleagues.

---

<p align="center">
  <strong>Made with ❤️ at the University of Brasília (UnB)</strong><br>
  <em>Bridging rigorous simulation and modern data science</em>
</p>
