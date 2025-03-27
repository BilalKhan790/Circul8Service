# Circul8Service - Lightweight System Telemetry Agent

A Windows-based telemetry agent developed as part of a Knowledge Transfer Partnership (KTP) project between the University of Hull and Techbuyer Ltd, co-funded by Innovate UK. It operates as a background service that collects key system metrics and transmits them to InfluxDB for visualization or AI-driven analysis, enabling data-driven decision-making for circular economy asset management.

## 🌱 Research Impact & Sustainability

This lightweight telemetry agent directly advances circular economy research by:
- Validating circular economy models with real-world performance metrics
- Establishing correlations between usage patterns and optimal refurbishment strategies
- Creating evidence-based frameworks for sustainable IT procurement decisions
- Enabling AI research on predictive maintenance and failure prevention
- Providing granular insights for organizational asset cascading strategies
- Demonstrating efficiency gains through lightweight monitoring and minimal data transfer
- Quantifying budget impacts of extended lifecycle management

## 🏗 Architecture Overview

Circul8Service operates as a Windows service with multiple collectors (Disk, Memory, EventLogs, Processor, SystemInfo) that leverage Windows Management Instrumentation (WMI) for system metrics collection. Each collector gathers specific metrics through WMI queries and sends them to the configured InfluxDB endpoint in App.config. Once in InfluxDB, these metrics can be visualized in Grafana or used by analytics tools to enable predictive maintenance and circular economy strategies.

### Dependencies
- **Windows Management Instrumentation (WMI)**: Core system metrics collection

## 🎯 Key Features

- **Comprehensive System Telemetry**
  - **Battery**: charge percentage, cycle count, capacity health
  - **Memory**: usage, available memory, page faults
  - **Processor**: CPU usage, process count, thread count
  - **Disk**: queue length, I/O performance, space usage
  - **System Info**: hardware details, OS information
  - **Event Logs**: application crashes, system events

## 🚀 Quick Start

1. **Download & Install**
   ```powershell
   # Run as administrator
   Circul8Service.exe --install
   ```

2. **Configure**
   - Edit `App.config` with your InfluxDB details
   - Import the [dashboard.json](dashboard.json) into Grafana

3. **View Your Data**
   - Open Grafana (http://localhost:3000)
   - Your metrics will start appearing automatically!

## 🔧 Detailed Setup

### Prerequisites
- Windows operating system
- .NET Framework 4.7.2 or higher
- InfluxDB 2.x
- Grafana 10.x

### Installation Options
```powershell
# Basic installation
Circul8Service.exe --install

# Complete installation with all options
Circul8Service.exe --install --collector_enabled="Disk,Memory,Eventlogs,Processor,SystemInfo" --frequency=3 --average_after=5
```

**Command-line Options**

| Option | Description |
|--------|-------------|
| `--install` | Install the service |
| `--uninstall` | Uninstall the service |
| `--collector_enabled="[list]"` | Enable specific collectors (comma-separated) |
| `--frequency=[seconds]` | Set collection frequency in seconds |
| `--average_after=[count]` | Set samples before aggregation |

### Uninstallation

To remove Circul8Service from your system:

1. **Uninstall the Service**
   ```powershell
   # Run as administrator
   Circul8Service.exe --uninstall
   ```

### Configuration
1. **InfluxDB Setup**
   ```xml
   <add key="InfluxDbUrl" value="https://localhost:8086" />
   <add key="InfluxDbOrg" value="your-org" />
   <add key="InfluxDbBucket" value="your-bucket" />
   <add key="InfluxDbToken" value="your-token" />
   ```

2. **Security Settings**
   ```xml
   <add key="CertificateVerification" value="true" />
   ```

## 🛠 Logging & Troubleshooting

Log Output: By default, logs are stored in a folder like C:\ProgramData\Circul8\Logs.

## 📈 Dashboard Preview

![Dashboard Preview](../main/Dashboard.PNG)

### Dashboard Sections
- **System Information**: Hardware specs, OS details, and uptime
- **Resource Utilization**: Real-time CPU, memory, and disk usage gauges
- **Performance Trends**: Historical graphs of system metrics
- **Battery Monitoring**: Voltage, charge percentage, and health metrics
- **Event Analysis**: Application errors and system event tracking

## 👥 Project Team

This project is one of the key outcomes of the Knowledge Transfer Partnership (KTP) between the University of Hull and Techbuyer

- **Bilal Khan** - KTP Associate, University of Hull
- **Astrid Wynne** - Head of Public Sector and Sustainability at Techbuyer
- **Nour Rteil** - Lead Developer at Techbuyer
- **Professor Dhaval Thakker** - Professor of AI and IoT at the University of Hull
- **Dr. Baseer Ahmad** - Lecturer in Robotics and Artificial Intelligence at the University of Hull

## 🙏 Acknowledgments

- University of Hull for academic support and research guidance
- Techbuyer Ltd for industry expertise and practical implementation
- Innovate UK for funding through the Knowledge Transfer Partnership program

## 📝 Contributing

We welcome contributions! Please feel free to submit a Pull Request. For major changes, please open an issue first to discuss what you would like to change.
