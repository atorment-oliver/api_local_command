#include "pch.h"
#include <windows.h>
#include <string>
#include <cstdint>
#include <stdexcept>
#include <iostream>
#include <vector>
#include <thread>
#include <chrono>
#include <devguid.h>
#include <setupapi.h>
#include <cfgmgr32.h>
#pragma comment(lib, "setupapi.lib")

class SerialPort {
private:
    HANDLE hComm;
    std::string currentPort;

    void ConfigurePort() {
        DCB dcb = { 0 };
        dcb.DCBlength = sizeof(DCB);

        if (!GetCommState(hComm, &dcb)) {
            throw std::runtime_error("Error GetCommState: " + std::to_string(GetLastError()));
        }

        dcb.BaudRate = CBR_9600;
        dcb.ByteSize = 8;
        dcb.Parity = NOPARITY;
        dcb.StopBits = ONESTOPBIT;
        dcb.fBinary = TRUE;
        dcb.fOutxCtsFlow = FALSE;
        dcb.fOutxDsrFlow = FALSE;
        dcb.fDtrControl = DTR_CONTROL_ENABLE;
        dcb.fRtsControl = RTS_CONTROL_ENABLE;
        dcb.fOutX = FALSE;
        dcb.fInX = FALSE;
        dcb.XonLim = 192;
        dcb.XoffLim = 192;

        if (!SetCommState(hComm, &dcb)) {
            throw std::runtime_error("Error SetCommState: " + std::to_string(GetLastError()));
        }

        COMMTIMEOUTS timeouts = { 0 };
        timeouts.ReadIntervalTimeout = 50;
        timeouts.ReadTotalTimeoutMultiplier = 10;
        timeouts.ReadTotalTimeoutConstant = 1000;
        timeouts.WriteTotalTimeoutMultiplier = 10;
        timeouts.WriteTotalTimeoutConstant = 1000;
        SetCommTimeouts(hComm, &timeouts);
    }

    bool ModifyPortState(bool enable) {
        HDEVINFO hDevInfo = SetupDiGetClassDevs(&GUID_DEVCLASS_PORTS, 0, 0, DIGCF_PRESENT);
        if (hDevInfo == INVALID_HANDLE_VALUE) {
            return false;
        }

        SP_DEVINFO_DATA devInfoData = { sizeof(SP_DEVINFO_DATA) };
        bool found = false;
        DWORD i = 0;

        while (SetupDiEnumDeviceInfo(hDevInfo, i++, &devInfoData)) {
            char buffer[256];
            if (SetupDiGetDeviceRegistryPropertyA(hDevInfo, &devInfoData, SPDRP_FRIENDLYNAME, NULL, (PBYTE)buffer, sizeof(buffer), NULL)) {
                std::string portName(buffer);
                if (portName.find(currentPort) != std::string::npos) {
                    found = true;
                    break;
                }
            }
        }

        if (!found) {
            SetupDiDestroyDeviceInfoList(hDevInfo);
            return false;
        }

        SP_PROPCHANGE_PARAMS params = { sizeof(SP_PROPCHANGE_PARAMS) };
        params.ClassInstallHeader.cbSize = sizeof(SP_CLASSINSTALL_HEADER);
        params.ClassInstallHeader.InstallFunction = DIF_PROPERTYCHANGE;
        params.StateChange = enable ? DICS_ENABLE : DICS_DISABLE;
        params.Scope = DICS_FLAG_CONFIGSPECIFIC;
        params.HwProfile = 0;

        if (!SetupDiSetClassInstallParams(hDevInfo, &devInfoData, &params.ClassInstallHeader, sizeof(params)) ||
            !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, hDevInfo, &devInfoData)) {
            SetupDiDestroyDeviceInfoList(hDevInfo);
            return false;
        }

        SetupDiDestroyDeviceInfoList(hDevInfo);
        return true;
    }

public:
    SerialPort() : hComm(INVALID_HANDLE_VALUE) {}

    ~SerialPort() {
        Close();
    }

    void SendCommand(const std::string& portName, const std::vector<uint8_t>& command) {
        Open(portName);
        SendHexCommand(command.data(), command.size());
    }

    void SendRepeatedCommand(const std::string& portName, const std::vector<uint8_t>& command, int repeatCount, int intervalMilliseconds) {
        Open(portName);
        for (int i = 0; i < repeatCount; ++i) {
            SendHexCommand(command.data(), command.size());
            if (i + 1 < repeatCount) {
                std::this_thread::sleep_for(std::chrono::milliseconds(intervalMilliseconds));
            }
        }
    }

    void SendCommands(const std::string& portName, const std::vector<std::vector<uint8_t>>& commands) {
        Open(portName);
        printf("COMANDO - ");
        for (const auto& command : commands) {
            printf("ENVIANDO - ");
            SendHexCommand(command.data(), command.size());
            std::this_thread::sleep_for(std::chrono::milliseconds(900));

            uint8_t response[256];
            ReadResponse(response, sizeof(response), 500);
        }
    }

    void Open(const std::string& portName = "COM4") {
        Close();
        currentPort = portName;

        std::string fullPortName = "\\\\.\\" + portName;
        hComm = CreateFileA(
            fullPortName.c_str(),
            GENERIC_READ | GENERIC_WRITE,
            0,
            NULL,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            NULL);

        if (hComm == INVALID_HANDLE_VALUE) {
            throw std::runtime_error("Error al abrir puerto: " + std::to_string(GetLastError()));
        }

        ConfigurePort();
        PurgeComm(hComm, PURGE_TXCLEAR | PURGE_RXCLEAR);
    }

    void Close() {
        if (hComm != INVALID_HANDLE_VALUE) {
            CloseHandle(hComm);
            hComm = INVALID_HANDLE_VALUE;
        }
    }

    void SendHexCommand(const uint8_t* data, size_t length) {
        if (hComm == INVALID_HANDLE_VALUE) {
            throw std::runtime_error("Puerto no abierto");
        }

        std::cout << "Enviando comando a " << currentPort << ": ";
        for (size_t i = 0; i < length; ++i) {
            printf("%02X ", data[i]);
        }
        std::cout << std::endl;

        DWORD bytesWritten;
        if (!WriteFile(hComm, data, static_cast<DWORD>(length), &bytesWritten, NULL)) {
            throw std::runtime_error("Error de escritura: " + std::to_string(GetLastError()));
        }

        if (bytesWritten != length) {
            throw std::runtime_error("Escritura incompleta");
        }
    }
    bool DisablePort(const std::string& portName = "COM3") {
        currentPort = portName;
        if (currentPort.empty()) {
            std::cerr << "No hay puerto abierto para deshabilitar" << std::endl;
            return false;
        }

        Close();

        if (!ModifyPortState(false)) {
            std::cerr << "Error al deshabilitar el puerto " << currentPort << std::endl;
            return false;
        }

        std::cout << "Puerto " << currentPort << " deshabilitado correctamente" << std::endl;
        return true;
    }

    bool EnablePort(const std::string& portName = "COM3") {
        currentPort = portName;
        if (currentPort.empty()) {
            std::cerr << "No hay puerto especificado para habilitar" << std::endl;
            return false;
        }

        if (!ModifyPortState(true)) {
            std::cerr << "Error al habilitar el puerto " << currentPort << std::endl;
            return false;
        }

        std::cout << "Puerto " << currentPort << " habilitado correctamente" << std::endl;
        Sleep(2000);
        return true;
    }
    size_t ReadResponse(uint8_t* buffer, size_t maxLength, DWORD timeoutMs = 1000) {
        if (hComm == INVALID_HANDLE_VALUE) {
            throw std::runtime_error("Puerto no abierto");
        }

        COMMTIMEOUTS timeouts = { 0 };
        timeouts.ReadIntervalTimeout = MAXDWORD;
        timeouts.ReadTotalTimeoutConstant = timeoutMs;
        SetCommTimeouts(hComm, &timeouts);

        DWORD bytesRead;
        if (!ReadFile(hComm, buffer, static_cast<DWORD>(maxLength), &bytesRead, NULL)) {
            throw std::runtime_error("Error de lectura: " + std::to_string(GetLastError()));
        }

        if (bytesRead > 0) {
            std::cout << "Respuesta de " << currentPort << " (" << bytesRead << " bytes): ";
            for (DWORD i = 0; i < bytesRead; ++i) {
                printf("%02X ", buffer[i]);
            }
            std::cout << std::endl;
        }

        return bytesRead;
    }
};

// Exportar la función que será llamada desde otras aplicaciones (como C#)
extern "C" __declspec(dllexport) int SendCreditRepeatsMDB(const char* portName, const uint8_t* commands, size_t length, int repeatCount, int intervalMilliseconds) {
    if (portName == nullptr || commands == nullptr || length == 0 || length > 256 ||
        repeatCount < 1 || repeatCount > 500 || intervalMilliseconds < 30 || intervalMilliseconds > 5000) {
        return 1; // Invalid arguments
    }
    try {
        SerialPort port;
        std::vector<uint8_t> commandVector(commands, commands + length);
        port.DisablePort(portName);
        port.EnablePort(portName);
        port.SendRepeatedCommand(portName, commandVector, repeatCount, intervalMilliseconds);
        return 0;
    }
    catch (const std::exception& e) {
        std::cerr << "Error: " << e.what() << std::endl;
        return 2; // Serial port or write failure
    }
    catch (...) {
        return 3; // Unknown native failure
    }
}

extern "C" __declspec(dllexport) void SendCommandsMDB(const char* portName, const uint8_t* commands, size_t length) {
    try {
        SerialPort port;

        // Verificar que el puntero de comandos no sea nulo
        if (commands == nullptr || length == 0) {
            throw std::invalid_argument("Los comandos no son válidos.");
        }

        // Dividir el array de comandos en fragmentos más pequeños, uno por cada comando
        // En este caso asumimos que cada comando tiene un tamaño de longitud fija
        // Aquí estamos tomando "length" como la longitud total de todos los comandos combinados
        size_t commandSize = 4; // Definir el tamaño del comando (por ejemplo, 8 bytes por comando)
        size_t numCommands = length / commandSize;

        std::vector<std::vector<uint8_t>> commandList;

        // Crear los comandos
        for (size_t i = 0; i < numCommands; ++i) {
            std::vector<uint8_t> command(commands + i * commandSize, commands + (i + 1) * commandSize);
            commandList.push_back(command);
        }

        // Si queda un comando parcial, agregarlo
        if (length % commandSize != 0) {
            std::vector<uint8_t> command(commands + numCommands * commandSize, commands + length);
            commandList.push_back(command);
        }

        // Deshabilitar y habilitar el puerto antes de enviar los comandos
        port.DisablePort(portName);
        port.EnablePort(portName);

        // Llamar al método SendCommands con el array de comandos
        port.SendCommands(portName, commandList);
    }
    catch (const std::exception& e) {
        std::cerr << "Error: " << e.what() << std::endl;
    }
}
extern "C" __declspec(dllexport) void SendCommandsMDB_32(const char* portName, const uint8_t* commands, size_t length) {
    try {
        SerialPort port;

        // Verificar que el puntero de comandos no sea nulo
        if (commands == nullptr || length == 0) {
            throw std::invalid_argument("Los comandos no son válidos.");
        }

        // Dividir el array de comandos en fragmentos más pequeños, uno por cada comando
        // En este caso asumimos que cada comando tiene un tamaño de longitud fija
        // Aquí estamos tomando "length" como la longitud total de todos los comandos combinados
        size_t commandSize = 4; // Definir el tamaño del comando (por ejemplo, 8 bytes por comando)
        size_t numCommands = length / commandSize;

        std::vector<std::vector<uint8_t>> commandList;

        // Crear los comandos
        for (size_t i = 0; i < numCommands; ++i) {
            std::vector<uint8_t> command(commands + i * commandSize, commands + (i + 1) * commandSize);
            commandList.push_back(command);
        }

        // Si queda un comando parcial, agregarlo
        if (length % commandSize != 0) {
            std::vector<uint8_t> command(commands + numCommands * commandSize, commands + length);
            commandList.push_back(command);
        }

        // Deshabilitar y habilitar el puerto antes de enviar los comandos
        port.DisablePort(portName);
        port.EnablePort(portName);

        // Llamar al método SendCommands con el array de comandos
        port.SendCommands(portName, commandList);
    }
    catch (const std::exception& e) {
        std::cerr << "Error: " << e.what() << std::endl;
    }
}
