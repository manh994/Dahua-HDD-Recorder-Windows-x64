Open NvrRecorderService.exe.config to config camera or NVR

Create service
open CMD as Administrator
type: 
sc create CamRecorder binpath= "%path_to_exe%\nvrrecorderservice.exe" start= auto
sc start CamRecorder

Delete service
open CMD as Administrator
type: 
sc delete CamRecorder