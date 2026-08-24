import socket
import json
import threading
import time

# Configuración de red (Invertida a la de C#)
UDP_IP = "127.0.0.1"
PORT_RECEIVE = 5005  # Python escucha aquí
PORT_SEND = 5006     # Python envía hacia C# aquí

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind((UDP_IP, PORT_RECEIVE))
sock.setblocking(False)

# Hilo para leer datos de AVEVA
def escuchar_aveva():
    while True:
        try:
            data, addr = sock.recvfrom(1024)
            datos = json.loads(data.decode('utf-8'))
            print(f"[AVEVA] Flujo SNK1: {datos['flujo_sink']:.4f} kg/s | Válvula actual: {datos['manpos_actual']:.2f}")
        except BlockingIOError:
            time.sleep(0.05) # Espera 50ms si no hay datos
        except Exception as e:
            pass

hilo = threading.Thread(target=escuchar_aveva, daemon=True)
hilo.start()

# Bucle principal para enviar comandos
print("=== PANEL DE CONTROL Python <-> AVEVA ===")
print("Escribe un valor entre 0.0 y 1.0 para cambiar la válvula (o 'q' para salir)")

while True:
    comando = input("Nueva posición de válvula: ")
    if comando.lower() == 'q':
        break
    try:
        # Validar y enviar
        val = float(comando)
        sock.sendto(str(val).encode('utf-8'), (UDP_IP, PORT_SEND))
        print(f">>> Comando {val} enviado a AVEVA.")
    except ValueError:
        print("Por favor, ingresa un número válido.")