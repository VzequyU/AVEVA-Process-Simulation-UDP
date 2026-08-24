import socket
import json
import threading
import time
import matplotlib.pyplot as plt
import matplotlib.animation as animation
from collections import deque

# --- CONFIGURACIÓN DE RED ---
UDP_IP = "127.0.0.1"
PORT_RECEIVE = 5005
PORT_SEND = 5006

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind((UDP_IP, PORT_RECEIVE))
sock.setblocking(False)

# --- MEMORIA PARA GRÁFICAS (Guarda los últimos 200 puntos) ---
MAX_PTS = 200
t_data = deque(maxlen=MAX_PTS)
h1_data = deque(maxlen=MAX_PTS)
h2_data = deque(maxlen=MAX_PTS)
h3_data = deque(maxlen=MAX_PTS)
h4_data = deque(maxlen=MAX_PTS)

start_time = time.time()

# --- HILO 1: ESCUCHAR A AVEVA ---
def escuchar_aveva():
    while True:
        try:
            data, addr = sock.recvfrom(1024)
            texto_recibido = data.decode('utf-8')
            
            try:
                datos = json.loads(texto_recibido)
                # Almacenar en las listas para graficar
                t_data.append(time.time() - start_time)
                h1_data.append(datos['h1'])
                h2_data.append(datos['h2'])
                h3_data.append(datos['h3'])
                h4_data.append(datos['h4'])
            except json.JSONDecodeError:
                print(f"\n[ERROR JSON] Recibido formato inválido: {texto_recibido}")
                
        except BlockingIOError:
            time.sleep(0.05)
        except Exception as e:
            print(f"\n[ERROR RED] {e}")

# --- HILO 2: CONSOLA DE COMANDOS ---
def consola_comandos():
    print("\n=== GEMELO DIGITAL: 4 TANQUES DE JOHANSSON ===")
    print("Ingresa los comandos con el formato Q1, Q2 (ejemplo: 0.08, 0.05)")
    print("Mira la ventana de gráficas para ver la respuesta dinámica.\n")
    
    while True:
        comando = input()
        try:
            partes = comando.split(',')
            if len(partes) == 2:
                q1 = float(partes[0].strip())
                q2 = float(partes[1].strip())
                
                json_cmd = json.dumps({"q1": q1, "q2": q2})
                sock.sendto(json_cmd.encode('utf-8'), (UDP_IP, PORT_SEND))
                print(f"[>] Inyectando escalón: Q1 = {q1} kg/s | Q2 = {q2} kg/s")
        except ValueError:
            print("Entrada no válida. Asegúrate de ingresar números.")

# Iniciar los procesos en segundo plano
threading.Thread(target=escuchar_aveva, daemon=True).start()
threading.Thread(target=consola_comandos, daemon=True).start()

# --- HILO PRINCIPAL: RENDERIZADO GRÁFICO (MATPLOTLIB) ---
fig, axs = plt.subplots(2, 2, figsize=(10, 6))
fig.canvas.manager.set_window_title('AVEVA Process Simulation - Gemelo Digital')
fig.suptitle('Respuesta Dinámica de las Columnas de Agua', fontsize=14)

# Inicializar líneas de cada tanque
line1, = axs[0, 0].plot([], [], 'b-', lw=2)
axs[0, 0].set_title('Tanque 1 (H1)')

line2, = axs[0, 1].plot([], [], 'r-', lw=2)
axs[0, 1].set_title('Tanque 2 (H2)')

line3, = axs[1, 0].plot([], [], 'g-', lw=2)
axs[1, 0].set_title('Tanque 3 (H3)')

line4, = axs[1, 1].plot([], [], 'm-', lw=2)
axs[1, 1].set_title('Tanque 4 (H4)')

# Formato común para todos los sub-gráficos
for ax in axs.flat:
    ax.grid(True, linestyle='--', alpha=0.7)
    ax.set_ylabel('Altura (m)')
    ax.set_xlabel('Tiempo (s)')
    ax.set_ylim(0, 1.0) # Ajusta este valor si tus tanques miden más de 1 metro

plt.tight_layout()

# Función que actualiza la gráfica iterativamente
def update(frame):
    if len(t_data) > 0:
        line1.set_data(t_data, h1_data)
        line2.set_data(t_data, h2_data)
        line3.set_data(t_data, h3_data)
        line4.set_data(t_data, h4_data)
        
        # Desplazamiento del eje X (efecto osciloscopio de 30 segundos)
        tiempo_actual = t_data[-1]
        for ax in axs.flat:
            ax.set_xlim(max(0, tiempo_actual - 30), max(30, tiempo_actual + 2))
            
    return line1, line2, line3, line4

# Ejecutar la animación interactiva con cache_frame_data=False para evitar el warning
ani = animation.FuncAnimation(fig, update, interval=100, blit=False, cache_frame_data=False)
plt.show()