#include <Arduino.h>
#include <BLE2902.h>
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <Wire.h>
#include <cstring>
#include <string>

namespace
{
  auto &mySerial = Serial;
  auto &myWire = Wire;

  constexpr int kSdaPin = 6;
  constexpr int kSclPin = 7;
  constexpr int kStreamSwitchPin = 3;
  constexpr int kHapticPin = 4; // 振動モータのドライバ入力ピンに合わせて変更してください。

  constexpr uint8_t kLsm6AddrGnd = 0x6A;
  constexpr uint8_t kLsm6AddrVcc = 0x6B;
  uint8_t sensorAddress = 0;

  constexpr uint8_t kRegWhoAmI = 0x0F;
  constexpr uint8_t kRegCtrl1 = 0x10;
  constexpr uint8_t kRegCtrl2 = 0x11;
  constexpr uint8_t kRegCtrl6 = 0x15;
  constexpr uint8_t kRegCtrl8 = 0x17;
  constexpr uint8_t kRegOutxLG = 0x22;

  constexpr char kDeviceName[] = "XIAO-LSM6DSV16X";
  constexpr char kServiceUuid[] = "6E400001-B5A3-F393-E0A9-E50E24DCCA9E";
  constexpr char kRxCharacteristicUuid[] = "6E400002-B5A3-F393-E0A9-E50E24DCCA9E";
  constexpr char kTxCharacteristicUuid[] = "6E400003-B5A3-F393-E0A9-E50E24DCCA9E";

  BLECharacteristic *txCharacteristic = nullptr;
  bool deviceConnected = false;
  bool streamEnabled = true;

  void scanI2C()
  {
    mySerial.println("I2C quick scan (0x6A / 0x6B)");
    myWire.beginTransmission(kLsm6AddrGnd);
    uint8_t status6A = myWire.endTransmission();
    myWire.beginTransmission(kLsm6AddrVcc);
    uint8_t status6B = myWire.endTransmission();

    mySerial.print("0x6A status = ");
    mySerial.println(status6A);
    mySerial.print("0x6B status = ");
    mySerial.println(status6B);
  }

  bool writeRegister(uint8_t reg, uint8_t value)
  {
    myWire.beginTransmission(sensorAddress);
    myWire.write(reg);
    myWire.write(value);
    return myWire.endTransmission() == 0;
  }

  bool readRegisters(uint8_t startReg, uint8_t *buffer, size_t length)
  {
    myWire.beginTransmission(sensorAddress);
    myWire.write(startReg);
    if (myWire.endTransmission(false) != 0)
    {
      return false;
    }

    const size_t received = myWire.requestFrom(sensorAddress, length);
    if (received != length)
    {
      return false;
    }

    for (size_t index = 0; index < length; ++index)
    {
      buffer[index] = myWire.read();
    }
    return true;
  }

  bool initLsm6dsv16x()
  {
    const uint8_t candidateAddresses[] = {kLsm6AddrGnd, kLsm6AddrVcc};
    uint8_t whoAmIValue = 0;
    bool sensorFound = false;

    for (uint8_t address : candidateAddresses)
    {
      sensorAddress = address;
      if (readRegisters(kRegWhoAmI, &whoAmIValue, 1))
      {
        mySerial.print("LSM6DSV16X address=0x");
        mySerial.print(sensorAddress, HEX);
        mySerial.print(" WHO_AM_I=0x");
        mySerial.println(whoAmIValue, HEX);
        if (whoAmIValue == 0x70)
        {
          sensorFound = true;
          break;
        }
      }
    }

    if (!sensorFound)
    {
      return false;
    }

    if (!writeRegister(kRegCtrl1, 0b00001001))
    {
      return false;
    }
    if (!writeRegister(kRegCtrl2, 0b00001001))
    {
      return false;
    }
    if (!writeRegister(kRegCtrl6, 0b00000100))
    {
      return false;
    }
    if (!writeRegister(kRegCtrl8, 0b10000010))
    {
      return false;
    }

    delay(10);
    return true;
  }

  bool readSensor(float &gx, float &gy, float &gz, float &ax, float &ay, float &az)
  {
    uint8_t raw[12];
    if (!readRegisters(kRegOutxLG, raw, sizeof(raw)))
    {
      return false;
    }

    int16_t values[6];
    for (int index = 0; index < 6; ++index)
    {
      values[index] = static_cast<int16_t>((raw[index * 2 + 1] << 8) | raw[index * 2]);
    }

    gx = static_cast<float>(values[0]) * 0.07f;
    gy = static_cast<float>(values[1]) * 0.07f;
    gz = static_cast<float>(values[2]) * 0.07f;
    ax = static_cast<float>(values[3]) * 0.000244f;
    ay = static_cast<float>(values[4]) * 0.000244f;
    az = static_cast<float>(values[5]) * 0.000244f;
    return true;
  }

  void updateStreamStateFromSwitch()
  {
    const bool shouldStream = (digitalRead(kStreamSwitchPin) == HIGH);
    if (shouldStream != streamEnabled)
    {
      streamEnabled = shouldStream;
      mySerial.print("STREAM ");
      mySerial.println(streamEnabled ? "ON" : "OFF");
    }
  }

  void setHapticOutput(bool enabled)
  {
    digitalWrite(kHapticPin, enabled ? HIGH : LOW);
  }

  class ServerCallbacks : public BLEServerCallbacks
  {
    void onConnect(BLEServer *server) override
    {
      deviceConnected = true;
    }

    void onDisconnect(BLEServer *server) override
    {
      deviceConnected = false;
      setHapticOutput(false);
      BLEDevice::startAdvertising();
    }
  };

  class RxCallbacks : public BLECharacteristicCallbacks
  {
    void onWrite(BLECharacteristic *characteristic) override
    {
      const std::string value = characteristic->getValue();
      if (value.empty())
      {
        return;
      }

      if (value[0] == '1')
      {
        setHapticOutput(true);
      }
      else if (value[0] == '0')
      {
        setHapticOutput(false);
      }
    }
  };
} // namespace

void setup()
{
  mySerial.begin(115200);
  delay(100);

  pinMode(kSdaPin, INPUT_PULLUP);
  pinMode(kSclPin, INPUT_PULLUP);
  myWire.begin(kSdaPin, kSclPin);
  myWire.setClock(100000);
  myWire.setTimeOut(20);

  pinMode(kStreamSwitchPin, INPUT_PULLUP);
  streamEnabled = (digitalRead(kStreamSwitchPin) == HIGH);

  mySerial.println("Start");
  scanI2C();

  pinMode(kHapticPin, OUTPUT);
  setHapticOutput(false);

  if (!initLsm6dsv16x())
  {
    mySerial.println("LSM6DSV16X init failed");
    while (true)
    {
      delay(1000);
    }
  }

  BLEDevice::init(kDeviceName);
  BLEServer *server = BLEDevice::createServer();
  server->setCallbacks(new ServerCallbacks());

  BLEService *service = server->createService(kServiceUuid);

  txCharacteristic = service->createCharacteristic(
      kTxCharacteristicUuid,
      BLECharacteristic::PROPERTY_NOTIFY | BLECharacteristic::PROPERTY_READ);
  txCharacteristic->addDescriptor(new BLE2902());

  BLECharacteristic *rxCharacteristic = service->createCharacteristic(
      kRxCharacteristicUuid,
      BLECharacteristic::PROPERTY_WRITE | BLECharacteristic::PROPERTY_WRITE_NR);
  rxCharacteristic->setCallbacks(new RxCallbacks());

  service->start();

  BLEAdvertising *advertising = BLEDevice::getAdvertising();
  advertising->addServiceUUID(kServiceUuid);
  advertising->setScanResponse(true);
  advertising->setMinPreferred(0x06);
  advertising->setMinPreferred(0x12);
  BLEDevice::startAdvertising();

  mySerial.println("BLE ready");
}

void loop()
{
  updateStreamStateFromSwitch();
  if (!streamEnabled)
  {
    delay(100);
    return;
  }

  float gx = 0.0f;
  float gy = 0.0f;
  float gz = 0.0f;
  float ax = 0.0f;
  float ay = 0.0f;
  float az = 0.0f;

  if (readSensor(gx, gy, gz, ax, ay, az))
  {
    char payload[96];
    snprintf(payload, sizeof(payload), "%.3f,%.3f,%.3f,%.3f,%.3f,%.3f", ax, ay, az, gx, gy, gz);
    mySerial.println(payload);

    if (deviceConnected && txCharacteristic != nullptr)
    {
      txCharacteristic->setValue(reinterpret_cast<uint8_t *>(payload), strlen(payload));
      txCharacteristic->notify();
    }
  }

  delay(20);
}