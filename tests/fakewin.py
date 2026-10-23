"""Fake Windows tool output: RTX 5080 (monitor) plus RTX 4070 Ti SUPER in an x4 slot."""

import json

NVIDIA_LIST = (
    "00000000:01:00.0, 0x2C0210DE, GPU-5080-uuid, NVIDIA GeForce RTX 5080\n"
    "00000000:21:00.0, 0x270510DE, GPU-4070-uuid, NVIDIA GeForce RTX 4070 Ti SUPER\n"
)
NVIDIA_LOAD = (
    "00000000:01:00.0, GPU-5080-uuid, 72, 11469, 16303, 16, 16, NVIDIA GeForce RTX 5080\n"
    "00000000:21:00.0, GPU-4070-uuid, 15, 2048, 16376, 4, 16, NVIDIA GeForce RTX 4070 Ti SUPER\n"
)
ADAPTERS = [
    {"Name": "NVIDIA GeForce RTX 5080", "PnpId": "PCI\\VEN_10DE&DEV_2C02&SUBSYS_1&REV_A1\\4&1",
     "Driver": "32.0.15.8000", "HRes": 2560, "Location": "PCI bus 1, device 0, function 0"},
    # Bus 33 decimal is 0x21, as nvidia-smi writes it.
    {"Name": "NVIDIA GeForce RTX 4070 Ti SUPER", "PnpId": "PCI\\VEN_10DE&DEV_2705&SUBSYS_2&REV_A1\\4&2",
     "Driver": "32.0.15.8000", "HRes": None, "Location": "PCI-Bus 33, Gerät 0, Funktion 0"},
    {"Name": "Microsoft Basic Display Adapter", "PnpId": "ROOT\\BASICDISPLAY\\0000",
     "Driver": "10.0", "HRes": None, "Location": None},
    {"Name": "AMD Radeon(TM) Graphics", "PnpId": "PCI\\VEN_1002&DEV_164E&SUBSYS_3&REV_C1\\4&3",
     "Driver": "31.0", "HRes": None, "Location": None},
]


def runner(adapters=ADAPTERS, nvidia_ok=True, powershell_ok=True):
    def run(cmd):
        if cmd[0] == "nvidia-smi":
            if not nvidia_ok:
                raise FileNotFoundError("nvidia-smi")
            query = cmd[1]
            if "pci.device_id" in query:
                return NVIDIA_LIST
            if "pcie.link.width.max" in query:
                return NVIDIA_LOAD
            return "pci.bus_id, name\n00000000:01:00.0, NVIDIA GeForce RTX 5080\n"
        if cmd[0] == "powershell":
            if not powershell_ok:
                raise FileNotFoundError("powershell")
            return json.dumps(adapters)
        raise FileNotFoundError(cmd[0])
    return run
