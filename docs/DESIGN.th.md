# Pay_Yet — บอท LINE เก็บเงิน (เอกสารออกแบบระบบ)

> ฉบับแปลภาษาไทยของ [DESIGN.md](DESIGN.md) — หากเนื้อหาไม่ตรงกัน ให้ยึดฉบับภาษาอังกฤษเป็นหลัก

## 1. เป้าหมาย

- เก็บเงินได้ทั้ง **รายคน** และ **สมาชิกในกลุ่ม LINE**
- **การสร้าง/จัดการรายการเก็บเงินทำได้เฉพาะในแชทส่วนตัว (1:1) กับบอทเท่านั้น** (กลุ่มไม่รก และป้องกันการใช้งานผิด)
- **แจ้งเตือนทวงอัตโนมัติ** จนกว่าลูกหนี้แต่ละคนจะจ่าย
- **เช็คยอด**: "ฉันค้างใครเท่าไหร่", "ใครยังค้างฉันบ้าง", "สถานะบิลของกลุ่มนี้"
- จ่ายผ่าน **QR พร้อมเพย์** + **ส่งสลิป** โดยคนเก็บเงินเป็นผู้ยืนยัน (หรือตรวจสลิปอัตโนมัติได้)

สิ่งที่ไม่ทำใน v1: โอนเงินแทนผู้ใช้, หลายสกุลเงิน, เชื่อมต่อธนาคารนอกเหนือจากการตรวจสลิป

## 2. สถาปัตยกรรม

```
LINE Platform ──webhook──▶ ASP.NET Core Web API (Plesk / IIS)
     ▲                         │  ├─ Webhook controller (ตรวจ signature)
     │ reply / push            │  ├─ Event router (user / group / postback)
     └─────────────────────────┤  ├─ Conversation state machine (wizard)
                               │  ├─ Bill service / Payment service
                               │  └─ Notification service (เรียก LINE REST ผ่าน HttpClient)
                               ▼
                         MySQL 8 ◀── Plesk Scheduled Task (ทุกชั่วโมง)
                               ▲        └─ POST /jobs/reminders (secret key) → push
                    App_Data/slips (ดิสก์บนเครื่อง, เข้าจากเว็บตรงๆ ไม่ได้)
                    Slip-verify API (เสริม: SlipOK / EasySlip)
หน้า LIFF: ให้แอปเดียวกันเสิร์ฟที่ /liff/* (หน้าจ่ายเงิน: QR + แนบสลิป; ภายหลัง: แดชบอร์ดบิล)
```

| ส่วนประกอบ | ตัวเลือก | หมายเหตุ |
|---|---|---|
| รูปแบบการโฮสต์ | **Single host**: แอป ASP.NET Core ตัวเดียว บน Plesk site / โดเมนเดียว | webhook, job, media, หน้า LIFF รวมอยู่ใน process เดียว |
| Runtime | ASP.NET Core Web API (.NET 8 LTS ขึ้นไป), **publish แบบ self-contained** | แนบ runtime ไปกับแอป ไม่ขึ้นกับเวอร์ชัน .NET บนโฮสต์ |
| LINE client | เขียน wrapper บางๆ ครอบ `HttpClient` (`IHttpClientFactory`) | LINE ไม่มี SDK ทางการสำหรับ .NET แต่ Messaging API เป็น REST ธรรมดา |
| ORM | EF Core + `Pomelo.EntityFrameworkCore.MySql` | ทำ migration ด้วย `dotnet ef` |
| ฐานข้อมูล | MySQL 8, `utf8mb4` | เก็บเงินเป็นจำนวนเต็มหน่วยสตางค์ (`BIGINT`) |
| Scheduler | Plesk Scheduled Task → "Fetch a URL" | IIS อาจหยุด/recycle app pool ทำให้ timer ในแอปไม่ทำงาน |
| QR | `QRCoder` | ส่งรูป PNG จาก `GET /qr/{token}.png` |
| Storage | ดิสก์บนเครื่อง `App_Data/slips` | อยู่นอก `wwwroot` |
| LIFF | LIFF app ภายใต้ channel **LINE Login** (ฟรี ใช้ได้กับ OA ที่ไม่ verify) เสิร์ฟจากแอปเดียวกัน | หน้าจ่ายเงิน: QR + แนบสลิป |
| HTTPS | Let's Encrypt ใน Plesk | webhook และ LIFF ต้องเป็น HTTPS |

## 3. กฎตามบริบทของแชท

| บริบท (`source.type`) | ทำอะไรได้ |
|---|---|
| `user` (1:1) | สร้าง/แก้ไข/ยกเลิกบิล, ยืนยันหรือปฏิเสธการจ่าย, ดูยอดทั้งหมด, ตั้งค่า |
| `group` | `ยอด` (สถานะบิลของกลุ่ม), ปุ่ม "จ่ายเงิน" บนการ์ดบิล → เปิด **หน้า LIFF สำหรับจ่ายเงิน** (QR + แนบสลิป) จึงไม่มีอะไรถูกโพสต์ในกลุ่มเพิ่ม, ไม่ต้องลงทะเบียนเอง; ถ้าสั่งสร้างบิลในกลุ่ม → บอทตอบว่า "กรุณาสร้างบิลในแชทส่วนตัวกับบอท" |

## 4. โฟลว์หลัก

### 4.1 ลงทะเบียนผู้ใช้และกลุ่ม
- เกิด event `follow` → upsert ลงตาราง `users` (displayName, รูปโปรไฟล์)
- บอทถูกเชิญเข้ากลุ่ม (`join`) → upsert ลง `groups` และ **ลงทะเบียนสมาชิกทุกคนอัตโนมัติ** ไม่ต้องกดปุ่ม:
  - เรียก `getGroupMemberIds` (แบ่งหน้า) + `getGroupMemberProfile` ทีละคน → upsert ลง `users` (`is_friend = 0` ถ้ายังไม่เคยเพิ่มบอทเป็นเพื่อน) และ `group_members`
  - event `memberJoined` / `memberLeft` ช่วยให้รายชื่อตรงกับกลุ่มเสมอ และทุกข้อความในกลุ่มจะ upsert ผู้ส่งด้วย
- **OA ที่ยังไม่ verify (กรณีปัจจุบัน)**: ใช้ `getGroupMemberIds` ไม่ได้ (เฉพาะ verified / premium) บอทจึง **เรียนรู้สมาชิกแบบ passive** แทน ตั้ง config `Line:CanListGroupMembers = false` เพื่อข้ามการเรียกดึงรายชื่อ ส่วนที่เหลือทำงานเหมือนเดิม
  - **แหล่งที่มา** (ทุกแหล่ง upsert `users` + `group_members` และดึงโปรไฟล์ด้วย `getGroupMemberProfile` ซึ่งใช้ได้กับสมาชิกทุกคนโดยไม่ต้องเป็นเพื่อน): `join` (คนที่เชิญบอท เมื่อ LINE ส่ง `source.userId` มา), `memberJoined`, **ทุก event ในกลุ่มที่มี `source.userId`** — ข้อความ รูป สติกเกอร์ postback — และการกดปุ่ม "จ่ายเงิน"
  - `memberLeft` ลบสมาชิกออก
  - **ติดตามความครอบคลุม**: เก็บผล `getGroupMembersCount` (ใช้ได้กับทุกบัญชี) ไว้ใน `groups.member_count` สมาชิกที่รู้จัก = `COUNT(group_members)` ความครอบคลุม = รู้จัก / ทั้งหมด
  - **ทักทายตอนเข้ากลุ่ม**: บอทโพสต์ครั้งเดียว "ให้ทุกคนพิมพ์อะไรก็ได้ 1 ครั้งเพื่อให้บอทรู้จัก" เป็นแค่การชวน ไม่ใช่ขั้นตอนลงทะเบียน คนที่เงียบจะยังไม่ถูกรู้จักจนกว่าจะพิมพ์ เข้ากลุ่ม หรือกดปุ่ม
  - **ตอนสร้างบิล** (ข้อ 4.2 ขั้น 4) คนเก็บเงินจะเห็น "รู้จักแล้ว 7/10 คน" และถ้าความครอบคลุมไม่ถึง 100% จะมีปุ่มให้บอทชวนทุกคนพิมพ์ทักทายในกลุ่มอีกครั้ง เลือกคนที่ยังไม่รู้จักเป็นลูกหนี้ไม่ได้
  - **เพิ่มสมาชิกที่เพิ่งรู้จักทีหลัง**: เมื่อรู้จักแล้ว คนเก็บเงินเพิ่มเข้าบิลที่เปิดอยู่ได้ด้วยการเพิ่มรายการใหม่ (ข้อ 4.2.1) ยอดหารเท่าของรายการเดิมจะไม่ถูกคำนวณใหม่เอง จึงไม่มียอดของใครเปลี่ยนโดยที่คนเก็บเงินไม่ได้สั่ง
  - ข้อสังเกต: กลุ่มส่วนใหญ่จะรู้จักสมาชิกเกือบครบภายใน 1-2 วันจากการคุยปกติ ถ้าภายหลัง verify บัญชีแล้ว ระบบจะเปลี่ยนไปดึงรายชื่อทั้งกลุ่มได้โดยไม่ต้องแก้ส่วนอื่น
- สมาชิกไม่ต้องเพิ่มบอทเป็นเพื่อนสำหรับบิลกลุ่ม (ทุกอย่างเกิดในกลุ่ม) ต้องเป็นเพื่อนเฉพาะบิล **รายคน** ที่ push ส่วนตัว

### 4.2 สร้างบิล — พิมพ์บรรทัดเดียว (ในแชท 1:1)
เป้าหมาย: **พิมพ์ข้อความเดียว + กดปุ่มเดียว** ต้องมีแค่ยอดเงิน ที่เหลือมีค่าเริ่มต้นให้หมด

คนเก็บเงินพิมพ์บรรทัดเดียว (`เก็บ` หรือ `เก็บเงิน`) ถ้ากดปุ่ม "เก็บเงิน" จาก rich menu บอทจะถามแค่ข้อเดียว: "ยอดเท่าไหร่?"

| พิมพ์ว่า | ความหมาย |
|---|---|
| `เก็บ ค่าอาหาร 200` | **เก็บ = ต่อคน**: คนที่เลือกจ่ายคนละ 200 (ปกติเลือก 1 คน) → บอทเปิดหน้าเลือกคน |
| `หาร ค่าเครื่องดื่ม 300` | **หาร = หารยอดรวม**: 300 หารตามจำนวนคนที่เลือก → หน้าเลือกคน |
| `หาร 600 ค่าข้าว ทุกคน` | หารทุกคนในกลุ่ม ไม่ต้องเลือก |
| `หาร 600 ยกเว้น สมชาย` | หารทุกคนยกเว้นสมชาย |
| `เก็บ 200 สมชาย` | ระบุชื่อเลย ไม่ต้องเลือก เก็บส่วนตัวจากคนเดียว |
| `เก็บ สมชาย 200 มาลี 300` | กำหนดยอดรายคน |
| `หาร 500 กลุ่มเพื่อน ...` | ระบุกลุ่มด้วยชื่อ (นับเป็น "ทุกคนในกลุ่มนั้น") |

**หน้าเลือกคน** (`liff/pay?mode=pick` ใช้เมื่อคำสั่งไม่ระบุชื่อใคร): บอทตอบพร้อมปุ่ม หน้า LIFF แสดงสมาชิกในกลุ่มพร้อมช่องติ๊ก (ค้นหา, เลือกทั้งกลุ่ม, สรุปสด เช่น "3 คน หารคนละประมาณ ฿100") เลือกที่แจ้งบิล (กลุ่มที่มีทุกคนที่เลือก หรือส่งส่วนตัว) `POST /api/pay/pick` ตรวจทุก id ซ้ำฝั่ง server (ต้องเป็นคนในกลุ่มของผู้เก็บ) แล้วบันทึกร่างและ push การ์ดยืนยันเข้าแชท

- **ค่าเริ่มต้น**: กลุ่ม = กลุ่มเดียวที่คนเก็บเงินอยู่ร่วมกับบอท ถ้ามีหลายกลุ่มใช้กลุ่มที่ใช้ล่าสุด ถ้าไม่มีให้กด Quick Reply เลือกชื่อกลุ่มหนึ่งครั้ง ลูกหนี้ = สมาชิกที่ **รู้จักแล้ว** ทุกคนยกเว้นคนเก็บเงิน ชื่อรายการ = "ยอดเรียกเก็บ <วันที่>" ไม่มีวันครบกำหนด (บอทเตือนทุกวัน 10:00 อยู่แล้ว ตั้งวันครบกำหนดทีหลังได้จากปุ่มบนการ์ด)
- **กฎเลือกปลายทาง**: ระบุแต่ชื่อคน → บิลรายคน (ส่วนตัว); ไม่ระบุชื่อ หรือระบุชื่อกลุ่ม → บิลกลุ่ม
- **การอ่านข้อความ**: ใช้กฎง่ายๆ ไม่ต้องพึ่ง AI — ยอดเงิน (รับ comma, `บาท`, `฿`, เลขไทย), คำสำคัญ `คนละ` / `ยกเว้น`, และชื่อคนที่จับคู่แบบใกล้เคียงกับชื่อของคนที่อยู่กลุ่มเดียวกับคนเก็บเงิน ชื่อกำกวมหรือไม่พบ → Quick Reply ให้เลือกหนึ่งครั้ง อ่านไม่ออก → บอทตอบตัวอย่าง 2 บรรทัด
- **การ์ดยืนยัน** (กดครั้งเดียว) แสดงผลที่คำนวณแล้วเสมอ ผิดพลาดแล้วเห็นทันที:
  ```
  เก็บเงิน: ค่าข้าว
  500 บาท ÷ 5 คน = 100 บาท/คน
  กลุ่ม: เพื่อนสนิท (รู้จักแล้ว 5/6 คน)
  [ยืนยัน]  [ยกเลิก]
  ```
  การ์ดจะขึ้น "เพิ่มเข้าบิลเดิม" เมื่อรวมยอด (ข้อ 4.2.1) และแสดงความครอบคลุมเฉพาะเมื่อต่ำกว่า 100% (ข้อ 4.1; ถ้า OA ไม่ verify จะมีปุ่ม "ให้บอทเรียกทุกคนพิมพ์ทักทาย")
- **ยกเลิกได้**: หลังยืนยัน คนเก็บเงินได้ปุ่ม `[ยกเลิกรายการนี้]` ใช้ได้ 10 นาที (ลบรายการออกถ้ายังไม่มีใครจ่าย)

เมื่อยืนยัน บอทโพสต์ **การ์ด Flex ลงกลุ่มใบเดียว** (รายการ, ยอดคงเหลือของแต่ละคน, ปุ่ม "จ่ายเงิน" ที่เปิดหน้า LIFF ตามข้อ 4.4) **ไม่ push ส่วนตัว** — บิลกลุ่มแจ้งและทวงในกลุ่มเท่านั้น

### 4.2.1 ทุกบิลแยกกัน การเตือนเป็นตัวรวม
- ทุกครั้งที่เก็บเงินจากกลุ่มหรือจากคน ระบบสร้าง **บิลใหม่** เสมอ (1 รายการ, 1 แถว `bill_items` ต่อลูกหนี้) ไม่รวมเข้าบิลเก่า แต่ละบิลจึงมีการ์ด การจ่าย ประวัติ และปุ่มยกเลิกของตัวเอง (`bills.scope_key` เลิกใช้ บิลเก่าที่เคยรวมไว้ยังใช้ได้)
- **การเตือนเป็นตัวรวมบิล** งานรายวันส่ง **การ์ดเดียวต่อกลุ่ม** สรุปทุกบิลที่ยังค้าง ("ค้างอยู่ N บิล · รวม ฿X" แต่ละบิลบอกผู้เก็บและยอด และรายชื่อว่าใครค้างกี่บิล) และ **ข้อความเดียวต่อคน** สำหรับบิลส่วนตัว แต่ละบิลมีรอบเตือนของตัวเอง (`remind_every_days`) เมื่อบิลใดบิลหนึ่งของกลุ่มถึงรอบ จะแสดงทุกบิลที่ค้างของกลุ่มนั้น

### 4.3 สร้างบิลรายคน (ในแชท 1:1)
- พิมพ์ชื่อลูกหนี้ในคำสั่งด่วน (`เก็บ 200 สมชาย`) ระบบจับคู่กับคนที่อยู่กลุ่มเดียวกับคนเก็บเงิน **หรือ**
- บอทสร้างรหัส/ลิงก์เชิญ (`https://line.me/R/oaMessage/@bot/?PAY-8F3K2`) ให้คนเก็บเงินส่งให้ใครก็ได้ เมื่อลูกหนี้เปิดลิงก์และส่งรหัสมา บิลจะผูกกับคนนั้นทันที
- บิลรายคนแจ้งและทวง **ส่วนตัว** กับลูกหนี้ (ต้อง `is_friend = 1` ถ้าไม่ใช่ บอทจะบอกคนเก็บเงินว่าลูกหนี้ต้องเพิ่มบอทเป็นเพื่อนก่อน) การเพิ่มยอดจะรวมเข้าบิลที่เปิดอยู่เดิม (ข้อ 4.2.1)

### 4.4 การจ่ายเงิน
การจ่ายเงินทำใน **หน้า LIFF** ไม่ใช่ในแชท กลุ่มจึงไม่รก (ไม่มี QR ไม่มีรูปสลิปถูกโพสต์)

1. ลูกหนี้กด **"จ่ายเงิน"** บนการ์ด (ปุ่มแบบ URI: `https://liff.line.me/<liffId>?b=<billToken>`) → หน้า LIFF เปิดใน LINE
2. หน้าเว็บระบุตัวผู้ใช้ผ่าน LIFF (`liff.getIDToken()`) แล้ว server ตรวจกับ LINE (`POST /oauth2/v2.1/verify`, `client_id` = Channel ID ของ Login) และใช้ `sub` เป็น LINE user ID หน้าจอแสดง: รายการในบิล, ยอด **คงเหลือ** ของผู้ใช้, QR พร้อมเพย์ (payload EMVCo สร้างฝั่ง server) พร้อมเลขพร้อมเพย์ของผู้รับให้คัดลอก และประวัติการจ่ายของผู้ใช้ในบิลนี้
3. ผู้ใช้จ่ายในแอปธนาคาร กลับมากด **"แนบสลิป"** (กล้อง/แกลเลอรี) แล้วส่ง หน้าเว็บย่อรูปฝั่ง client (JPEG ≤ 1600 px) แล้วอัปโหลดไป `POST /api/pay/slip` (multipart, ID token ใน `Authorization`) สถานะเป็น `SUBMITTED` และหน้าขึ้น "รอผู้เก็บตรวจสอบ"
4. (เสริม) Slip-verify API ตรวจยอดเงิน + บัญชีผู้รับ → ถ้าตรงอนุมัติอัตโนมัติ ถ้าไม่ตรงรอตรวจด้วยมือ
5. คนเก็บเงินได้รับ push **ส่วนตัว** พร้อมรูปสลิป (ส่งผ่าน `/media/{token}` ที่หมดอายุเร็ว) → **[ยืนยัน] [ปฏิเสธ]** (คนเก็บเงินเป็นเพื่อนกับบอทอยู่แล้วเสมอ)
6. เมื่ออนุมัติ: `paid_satang += ยอด`; รายการเป็น `PAID` เมื่อ `paid ≥ ยอดรวม` ลูกหนี้เห็นผลในหน้า LIFF (และได้ push ส่วนตัวถ้า `is_friend = 1`) **ไม่โพสต์อะไรลงกลุ่มทุกครั้งที่มีคนจ่าย** สถานะจ่าย/ยังไม่จ่ายจะไปปรากฏในสรุป 10:00 รอบถัดไปและในคำสั่ง `ยอด` เมื่อทุกคนจ่ายครบ บิลเป็น `CLOSED` และโพสต์สรุปหนึ่งบรรทัดลงกลุ่ม
7. บิลรายคนใช้หน้า LIFF เดียวกัน (ปุ่มในแชทส่วนตัว) การส่งรูปสลิปตรงๆ ในแชท 1:1 ยังรับเป็นทางสำรอง เมื่อผู้ใช้มีรายการค้างจ่ายเพียงรายการเดียว

การตรวจฝั่ง server ที่ `POST /api/pay/slip`: ผู้ใช้ที่ตรวจ token แล้วต้องเป็น `debtor_id` ของรายการนั้น; รายการต้องเป็น `PENDING`/`REJECTED`/จ่ายไม่ครบ; ไฟล์ ≤ 5 MB เป็น JPEG/PNG ตาม magic bytes (ไม่ดูนามสกุล); เก็บ SHA-256 ใน `payments.slip_hash` (unique) กันส่งรูปเดิมซ้ำ; จำกัดความถี่ต่อผู้ใช้

### 4.4.0 หน้าจอในแอป (LIFF)
หน้า LIFF เดียว แยกหลายหน้าจอด้วย `?mode=`: `home` (หน้าหลัก: เก็บเงินใหม่ จัดการบิล ที่ฉันต้องจ่าย ตั้งพร้อมเพย์), `new` (ฟอร์ม), `manage` (ข้อ 4.4.1), `owe` และ `pick` (หน้าเลือกคนของคำสั่งที่พิมพ์) ถ้าไม่มีพารามิเตอร์ จะเปิด `home`
- **ฟอร์มสร้างบิล** — `GET /api/app/new` (กลุ่ม สมาชิก รูป และสถานะพร้อมเพย์) → เลือกกลุ่ม → ชื่อรายการ ยอดเงิน "หารยอดรวม" หรือ "เก็บคนละ" เลือกคน ที่แจ้งบิล → `POST /api/app/create` เซิร์ฟเวอร์ตรวจคนที่เลือกซ้ำ แล้ว `BillCreationService` สร้างและแจ้งบิลเหมือนปุ่มยืนยันในแชททุกประการ ผลลัพธ์มีปุ่มยกเลิก (`POST /api/app/undo` ภายใน 10 นาที)
- **พร้อมเพย์** — `POST /api/app/promptpay` (ผู้เก็บต้องตั้งก่อนบิลแรก แสดงเลขแบบปิดบังบางส่วน)
- **ที่ฉันต้องจ่าย** — `GET /api/app/owe` แสดงรายการค้างของผู้ใช้ พร้อม token สำหรับเปิดหน้าจ่ายเงิน
- ใครเปิดแอปก่อนเคยคุยกับบอท ระบบสร้างผู้ใช้จากโปรไฟล์ LINE ใน ID token
ปุ่มใน Rich menu ชี้ตรงไปที่ `https://liff.line.me/<liffId>?mode=home|new|manage|owe` ได้

### 4.4.1 จัดการบิลหลังสร้างแล้ว
พิมพ์ `จัดการ` ในแชทส่วนตัวเพื่อเปิดหน้า LIFF `?mode=manage` แสดงบิลที่ยังเปิดอยู่ของผู้เก็บ พร้อมรายการ รายชื่อ สถานะ และยอดที่ยังค้าง
- **รับเงินสด** — `POST /api/manage/paid {itemId, amount?}` เพิ่มการจ่ายสถานะ `APPROVED` (`verified_by = CASH` ไม่มีสลิป) และปรับ `paid_satang` จ่ายบางส่วนได้ ปฏิเสธถ้าคนนั้นมีสลิปรอตรวจ (`SUBMITTED`)
- **ยกหนี้** — ยกยอดที่ยังค้างของคนนั้น (`WAIVED`) ส่วนที่จ่ายแล้วคงเดิม
- **ยกเลิกรายการเดียว** — ไม่จำกัดเวลา (ปุ่มยกเลิกในแชทจำกัด 10 นาที) ยอดของทุกคนลดลงตามส่วนแบ่ง ปฏิเสธถ้ามีสลิปรอตรวจหรือมีคนจ่ายเกินยอดใหม่ คนที่จ่ายพอแล้วจะเป็น `PAID` ถ้ายกเลิกรายการสุดท้าย บิลจะถูกยกเลิกด้วย
- **ยกเลิกทั้งบิล** — สถานะ `CANCELLED` ปล่อย scope เงินที่รับไปแล้วบอทไม่ได้คืนให้
ทุกการกระทำตรวจซ้ำว่าผู้เรียกเป็นผู้สร้างบิล บิลกลุ่มแจ้งการเปลี่ยนแปลงในกลุ่ม บิลส่วนตัวแจ้งลูกหนี้ที่เป็นเพื่อนกับบอท

### 4.5 การแจ้งเตือนทวงเงิน
- **วันละครั้ง เวลา 10:00 (Asia/Bangkok)** สำหรับทุกบิล OPEN ที่ยังมีรายการค้างจ่าย เริ่มตั้งแต่วันถัดจากวันที่สร้างบิล
- Plesk Scheduled Task เรียก `POST /jobs/reminders` (header `X-Job-Key`) **ทุกชั่วโมง** job จะไม่ทำอะไรก่อน 10:00 เวลาไทย หลังจากนั้นแต่ละบิล OPEN จะ "จองสิทธิ์" ของวันนั้นแบบ atomic:
  `UPDATE bills SET last_reminded_on = <วันนี้ตามเวลาไทย> WHERE id = ? AND (last_reminded_on IS NULL OR last_reminded_on < <วันนี้>)` — ส่งเฉพาะเมื่ออัปเดตได้ 1 แถว จึงไม่ส่งซ้ำ และถ้าพลาดช่วง 10:00 (เซิร์ฟเวอร์ล่ม) รอบถัดไปจะส่งชดเชยให้
- **บิลกลุ่ม** → ข้อความเดียวใน **กลุ่ม**: รายชื่อ "ยังไม่จ่าย" พร้อมยอดคงเหลือรายคน (ติด ⚠️ เมื่อเลย `due_at`) push ต่อกลุ่มครั้งเดียว ไม่ส่งส่วนตัว
- **บิลรายคน** → push **ส่วนตัว** ถึงลูกหนี้หนึ่งครั้ง
- การเพิ่มยอดแจ้งในกลุ่มทันที (ไม่รอ 10:00) ส่วนผลการจ่ายรายคน **ไม่** โพสต์ลงกลุ่ม (ข้อ 4.4)
- คนเก็บเงินกด **"ทวงตอนนี้"** เองได้ (จำกัด 1 ครั้งต่อ 6 ชั่วโมงต่อบิล)

### 4.6 การเช็คยอด
| คำสั่ง | ใช้ที่ | ผลลัพธ์ |
|---|---|---|
| `ยอด` / `ยอดค้าง` | 1:1 | ยอดที่ฉันค้าง (แยกตามคนเก็บ) + ยอดที่คนอื่นค้างฉัน |
| `ยอด` | กลุ่ม | สถานะบิลที่ยังเปิดอยู่ในกลุ่มนี้ (จ่ายแล้ว ✅ / รอจ่าย ⏳) |
| `ประวัติ` | 1:1 | บิลที่ปิดแล้ว 10 รายการล่าสุด |
| Rich menu | 1:1 | ปุ่ม: เก็บเงิน · ยอดของฉัน · บิลที่ฉันเก็บ · ตั้งค่า |

## 5. โครงสร้างข้อมูล

MySQL 8, InnoDB, `utf8mb4_unicode_ci` (รองรับชื่อภาษาไทยและอีโมจิ) ค่า `DATETIME` ทั้งหมดเก็บเป็น UTC

```sql
CREATE TABLE users (
  id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  line_user_id  VARCHAR(64)  NOT NULL UNIQUE,
  display_name  VARCHAR(255),
  promptpay_id  VARCHAR(20),                 -- phone / national ID for receiving money
  is_friend     TINYINT(1)   NOT NULL DEFAULT 1,
  created_at    DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
);

CREATE TABLE `groups` (                      -- GROUPS is a reserved word in MySQL 8
  id             BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  line_group_id  VARCHAR(64)  NOT NULL UNIQUE,
  name           VARCHAR(255),
  member_count   INT NULL,                         -- from getGroupMembersCount (refreshed on events, ≤ once/hour)
  active         TINYINT(1)   NOT NULL DEFAULT 1   -- 0 after bot leaves
);

CREATE TABLE group_members (
  group_id   BIGINT UNSIGNED NOT NULL,
  user_id    BIGINT UNSIGNED NOT NULL,
  joined_at  DATETIME(3) NOT NULL,                  -- first time the bot saw this member
  source     ENUM('LISTED','JOINED','SEEN') NOT NULL, -- how the bot learned of them
  last_seen_at DATETIME(3) NULL,
  PRIMARY KEY (group_id, user_id),
  FOREIGN KEY (group_id) REFERENCES `groups`(id),
  FOREIGN KEY (user_id)  REFERENCES users(id)
);

CREATE TABLE bills (
  id                      BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  creator_id              BIGINT UNSIGNED NOT NULL,
  group_id                BIGINT UNSIGNED NULL,          -- NULL = individual bill
  title                   VARCHAR(255) NOT NULL,         -- from the first charge; items live in bill_charges
  due_at                  DATETIME(3),
  last_reminded_on        DATE NULL,                     -- Bangkok date of the last daily reminder
  status                  ENUM('OPEN','CLOSED','CANCELLED') NOT NULL DEFAULT 'OPEN',
  scope_key               VARCHAR(80) NULL UNIQUE,       -- set only while OPEN: 'G:<creator>:<group>' or 'P:<creator>:<debtor>' → one open bill per scope; NULL when closed
  invite_code             VARCHAR(16) NULL UNIQUE,
  created_at              DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  FOREIGN KEY (creator_id) REFERENCES users(id),
  FOREIGN KEY (group_id)   REFERENCES `groups`(id)
);

CREATE TABLE bill_items (                    -- one row per debtor
  id              BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  bill_id         BIGINT UNSIGNED NOT NULL,
  debtor_id       BIGINT UNSIGNED NULL,       -- NULL until invite code is claimed
  amount_satang   BIGINT NOT NULL,              -- sum of this debtor's charge_shares
  paid_satang     BIGINT NOT NULL DEFAULT 0,    -- remaining = amount_satang - paid_satang
  status          ENUM('PENDING','SUBMITTED','PAID','REJECTED','WAIVED') NOT NULL DEFAULT 'PENDING',
  paid_at         DATETIME(3),
  FOREIGN KEY (bill_id)   REFERENCES bills(id),
  FOREIGN KEY (debtor_id) REFERENCES users(id),
  UNIQUE KEY uq_item (bill_id, debtor_id)
);

CREATE TABLE bill_charges (                  -- each "add money" action; many per bill
  id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  bill_id       BIGINT UNSIGNED NOT NULL,
  title         VARCHAR(255) NOT NULL,
  total_satang  BIGINT NOT NULL,
  split_mode    ENUM('EQUAL','CUSTOM') NOT NULL,
  created_at    DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  FOREIGN KEY (bill_id) REFERENCES bills(id)
);

CREATE TABLE charge_shares (                 -- who owes how much for each charge
  charge_id      BIGINT UNSIGNED NOT NULL,
  bill_item_id   BIGINT UNSIGNED NOT NULL,
  amount_satang  BIGINT NOT NULL,
  PRIMARY KEY (charge_id, bill_item_id),
  FOREIGN KEY (charge_id)    REFERENCES bill_charges(id),
  FOREIGN KEY (bill_item_id) REFERENCES bill_items(id)
);

CREATE TABLE payments (
  id             BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  bill_item_id   BIGINT UNSIGNED NOT NULL,
  slip_path      VARCHAR(255) NOT NULL,      -- relative path under App_Data/slips
  slip_ref       VARCHAR(64) NULL UNIQUE,    -- transRef from slip; blocks reused slips
  slip_hash      CHAR(64) NOT NULL UNIQUE,   -- SHA-256 of the image; blocks identical re-uploads
  amount_satang  BIGINT,
  verified_by    ENUM('AUTO','CREATOR'),
  status         ENUM('SUBMITTED','APPROVED','REJECTED') NOT NULL DEFAULT 'SUBMITTED',
  created_at     DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  FOREIGN KEY (bill_item_id) REFERENCES bill_items(id)
);

CREATE TABLE conversation_state (            -- wizard progress in 1:1 chat
  user_id     BIGINT UNSIGNED PRIMARY KEY,
  flow        VARCHAR(32) NOT NULL,          -- CREATE_BILL | SUBMIT_SLIP ...
  step        VARCHAR(32) NOT NULL,
  data        JSON,
  expires_at  DATETIME(3) NOT NULL,          -- reset after 30 min idle
  FOREIGN KEY (user_id) REFERENCES users(id)
);

CREATE TABLE processed_events (              -- idempotency; purge rows older than 7 days
  webhook_event_id VARCHAR(64) PRIMARY KEY,
  processed_at     DATETIME(3) NOT NULL
);
```

ID ภายในเป็น auto-increment และถูกใส่ไว้ใน postback จึงต้องตรวจสิทธิ์ความเป็นเจ้าของฝั่ง server ทุกครั้ง (ดูข้อ 8) ส่วน token ที่เปิดเผยภายนอก (รหัสเชิญ, URL ของ QR/สลิป) ต้องสุ่ม ห้ามใช้ ID ที่เรียงลำดับ

การหารเท่า (ต่อรายการ): แต่ละคนได้ `floor(total / n)` สตางค์ ส่วนเศษที่เหลือบวกให้ลูกหนี้คนแรกๆ เพื่อให้ผลรวมเท่ากับยอดของรายการนั้นเสมอ

การเพิ่มรายการทำใน transaction เดียว: ล็อกบิล OPEN ด้วย `scope_key` → insert `bill_charges` + `charge_shares` → upsert `bill_items` ของลูกหนี้แต่ละคนแล้วบวก `amount_satang` → เปิดรายการที่เป็น `PAID` แต่ `paid_satang < amount_satang` กลับเป็น `PENDING`

## 6. State machine

```
bill_item:  PENDING ──ส่งสลิป──▶ SUBMITTED ──ยืนยัน──▶ PAID
               ▲                     │
               └──── REJECTED ◀──────┘ ปฏิเสธ (ลูกหนี้ส่งใหม่ได้)
            PENDING/REJECTED ──คนเก็บยกหนี้ให้──▶ WAIVED
            PAID ──เพิ่มรายการจนจ่ายไม่ครบ (paid < total)──▶ PENDING

bill:       OPEN ──ทุกรายการ PAID/WAIVED──▶ CLOSED (scope_key = NULL)
            OPEN ──คนเก็บยกเลิก────────────▶ CANCELLED (scope_key = NULL, แจ้งที่เดียวกับที่บิลอยู่)
            OPEN ──เพิ่มรายการ──▶ OPEN (บิลเดิม คำนวณยอดใหม่)
```

## 7. ข้อจำกัดของแพลตฟอร์ม LINE

- **โควตา push message**: แพ็กเกจฟรีมีโควตาต่อเดือนน้อย และการ push เข้ากลุ่มนับตามจำนวนสมาชิกในกลุ่ม
  → ใช้ **reply** (ฟรี) ทุกครั้งที่ตอบ event จากผู้ใช้ บิลกลุ่มแจ้งและทวงด้วย **ข้อความในกลุ่มวันละหนึ่งครั้ง** ต้นทุนจึงขึ้นกับจำนวนกลุ่ม ไม่ใช่จำนวนลูกหนี้ ควรเทียบจำนวนสมาชิกของกลุ่มใหญ่กับโควตาของแพ็กเกจ
- push หาผู้ใช้ได้เฉพาะคนที่ **เพิ่มบอทเป็นเพื่อน** แล้ว (`is_friend` อัปเดตจาก event `follow`/`unfollow`) บิลกลุ่มไม่ติดข้อจำกัดนี้
- `getGroupMemberIds` (ดึงรายชื่อสมาชิกทั้งกลุ่ม) ต้องใช้บัญชี verified/premium; OA ที่ไม่ได้ verify ใช้การเรียนรู้แบบ passive พร้อมติดตามความครอบคลุม (ดูข้อ 4.1)
- แก้ไขข้อความที่ส่งไปแล้วไม่ได้ → ต้องส่งการ์ดสถานะใหม่แทน
- reply token ใช้ได้ในเวลาสั้นๆ → ตอบให้เร็ว และทำงานหนักแบบ async

## 8. ความปลอดภัยและความเสถียร

- ตรวจ `X-Line-Signature` ทุก webhook
- endpoint ของ LIFF (`/api/pay/*`) รับเฉพาะ LINE ID token ที่ตรวจฝั่ง server กับ Login channel แล้วเท่านั้น ห้ามเชื่อ user ID ที่ส่งมาใน body หรือ URL
- กันการประมวลผลซ้ำด้วย `webhookEventId` (`processed_events`) เพราะ LINE อาจส่ง event ซ้ำ
- สิทธิ์: เฉพาะ `bills.creator_id` แก้ไข/ยกเลิก/ยืนยันได้; เฉพาะ `bill_items.debtor_id` ส่งสลิปได้
- ข้อมูลใน postback มีแค่ ID (`action=approve&payment=<id>`) และต้องตรวจสิทธิ์กับสถานะปัจจุบันฝั่ง server ทุกครั้ง
- ปฏิเสธสลิปซ้ำด้วย `slip_ref`; ตรวจยอดเงินและบัญชีผู้รับเมื่อตรวจอัตโนมัติ
- เก็บเงินเป็นจำนวนเต็มหน่วยสตางค์; เก็บเวลาเป็น UTC และแสดงผลเป็นเวลาไทย (Asia/Bangkok)
- รูปสลิปเก็บที่ `App_Data/slips` (เข้าจากเว็บตรงๆ ไม่ได้) แต่ข้อความรูปภาพของ LINE ต้องใช้ URL HTTPS สาธารณะ จึงส่งรูปสลิปและ QR ผ่าน `GET /media/{token}` ที่ใช้ token สุ่มและหมดอายุเร็ว
- endpoint `/jobs/*` ต้องมี header ลับ `X-Job-Key` ถ้าไม่มีให้ปฏิเสธทั้งหมด

## 9. โครงสร้างโปรเจกต์ (ข้อเสนอ)

```
PayYet.sln
src/PayYet.Api/
  Program.cs                       # DI, routing, config binding
  Controllers/
    LineWebhookController.cs       # POST /api/line/webhook
    JobsController.cs              # POST /jobs/reminders (X-Job-Key)
    MediaController.cs             # GET /media/{token} (slip images for the collector's push)
    PayController.cs               # GET /api/pay/{billToken}, POST /api/pay/slip (LIFF, ID-token auth)
  Line/
    LineMessagingClient.cs         # reply / push / profile / content download (HttpClient)
    LineSignatureValidator.cs      # HMAC-SHA256 of raw body vs X-Line-Signature
    Flex/                          # Flex message builders (bill card, QR, summary)
  Handlers/
    UserMessageHandler.cs          # 1:1 commands + wizard
    GroupMessageHandler.cs         # group commands
    PostbackHandler.cs             # button actions
    LifecycleHandler.cs            # follow/unfollow/join/leave/memberJoined
  Services/
    BillService.cs
    PaymentService.cs
    ReminderService.cs
    PromptPayPayload.cs            # EMVCo payload + CRC16
    SlipVerifyClient.cs            # optional external API
  Data/
    AppDbContext.cs
    Entities/
    Migrations/
  Pages/Liff/                      # LIFF pay page (Razor Pages + small JS using the LIFF SDK)
  wwwroot/                         # static assets for LIFF pages
tests/PayYet.Tests/
```

## 10. หมายเหตุการ deploy บน Plesk

- **Single host**: ทุกอย่างรันในแอปเดียว บน Plesk site เดียว โดเมนเดียว deploy ครั้งเดียว
  - เส้นทาง: `/api/line/webhook`, `/jobs/*`, `/media/*`, `/liff/*` — อยู่ origin เดียวกัน จึงไม่ต้องตั้งค่า CORS URL สาธารณะ = `https://api.drivetodev.online/...` ดูหัวข้อ "การตัดสินใจเรื่องโฮสต์" ด้านล่าง
  - IIS app pool เดียว = instance เดียว จึงเก็บ state ในหน่วยความจำได้ (คิว `Channel<T>`, cache สำหรับจำกัดความถี่) ห้ามเปิด web garden หรือหลาย worker process
  - อะไรที่ต้องอยู่รอดหลังรีสตาร์ท (สถานะ wizard, สลิปที่รอตรวจ, กำหนดการเตือน) ต้องเก็บใน MySQL ไม่ใช่ในหน่วยความจำ
  - ถ้าวันหนึ่งต้องรันหลาย instance ให้ย้ายคิวและ cache ไป MySQL ก่อน

- โฮสต์: Plesk **Windows** build บนเครื่องตัวเองแบบ **self-contained** แล้วอัปโหลดโฟลเดอร์ผลลัพธ์ขึ้นไป (FTP / Web Deploy / zip ผ่าน File Manager)
  ```bash
  dotnet publish src/PayYet.Api -c Release -r win-x64 --self-contained true -o publish
  ```
  - runtime ของ .NET ถูกแนบไปด้วย โฮสต์จึงต้องมีแค่ IIS ที่มี **ASP.NET Core Module (ANCM)** ซึ่ง Plesk ที่รองรับ ASP.NET Core มีอยู่แล้ว ไฟล์ `web.config` ที่ได้จะชี้ไปที่ `PayYet.Api.exe`
  - ห้ามใช้ `PublishSingleFile` ให้คงโครงสร้างโฟลเดอร์ปกติไว้สำหรับ IIS
  - ใช้ `win-x64` **ตรวจบนโฮสต์แล้ว: app pool ปัจจุบันรันแบบ 32-bit** (`Is64BitProcess = False`) ต้องปิด "Enable 32-bit applications" ของ app pool โดเมนนี้ (Plesk → IIS Application Pool) หรือเปลี่ยนไป publish แบบ `win-x86` โฮสต์ลง ASP.NET Core runtime 8.0 / 9.0 / 10.0 ไว้แล้ว จึง publish แบบพึ่ง runtime ของโฮสต์ก็ได้ แต่ยังใช้ self-contained เป็นค่าเริ่มต้น
  - identity ของ app pool (`IWPD_289`) ใช้ร่วมกันทุกเว็บใน subscription `thaipesleague.com` โค้ดของเว็บอื่นจึงอ่านไฟล์ของแอปนี้ได้ ควรเก็บค่าลับใน environment variable หรือ `appsettings.Production.json` นอก web root ถ้าทำได้ และถือว่า `App_Data/slips` เว็บอื่นใน subscription เดียวกันอ่านได้
  - ขนาดไฟล์ราว 70–100 MB; การอัปเดตความปลอดภัยของ runtime ทำได้โดย build ใหม่ด้วย SDK ที่ใหม่กว่าแล้ว deploy ซ้ำ
  - ตอน deploy ซ้ำ ให้อัปโหลด `app_offline.htm` ก่อนเพื่อกันไฟล์ถูกล็อก แทนที่ไฟล์ แล้วค่อยลบออก
- **การตัดสินใจเรื่องโฮสต์:** แรกเริ่มตั้งใจใช้ `https://drivetodev.online/lineapi` แต่ IIS ล็อก `system.webServer/handlers` ของเว็บนั้น `web.config` ในโฟลเดอร์ย่อยจึงลงทะเบียน handler ของ ASP.NET Core ไม่ได้ (HTTP 500.19, lock violation 0x80070021) จึงย้ายไปใช้ subdomain **`https://api.drivetodev.online`** (โฟลเดอร์ docroot `api.drivetodev.online`) ซึ่งไม่มีเว็บอื่นอยู่
  - `App:PathBase` เว้นว่างไว้ ส่วน `app.UsePathBase(...)` ยังรองรับกรณีโฮสต์ในโฟลเดอร์ย่อย ถ้าวันหนึ่งปลดล็อกได้
  - ห้ามเขียน URL ที่ขึ้นต้นด้วย `/` ตรงๆ สำหรับลิงก์ที่ส่งให้ LINE ให้สร้างจาก `App:PublicBaseUrl = https://api.drivetodev.online`
  - URL สาธารณะ: webhook `https://api.drivetodev.online/api/line/webhook`; LIFF endpoint `https://api.drivetodev.online/liff/pay`; job เตือน `https://api.drivetodev.online/jobs/reminders?key=...`
  - subdomain ต้องมีใบรับรอง TLS ของตัวเอง (Plesk → api.drivetodev.online → SSL/TLS Certificates → Let's Encrypt) เพราะ LINE ไม่รับใบรับรองที่ไม่ถูกต้อง
- Webhook URL: `https://api.drivetodev.online/api/line/webhook` เปิด Let's Encrypt และ "redirect HTTP → HTTPS" ใน Plesk
- ตั้งค่า LIFF: สร้าง channel **LINE Login** ใน provider เดียวกัน เพิ่ม LIFF app (size Full, scope `openid profile`, endpoint `https://api.drivetodev.online/liff/pay`) และเชื่อม Login channel กับบอท config: `Liff:Id`, `Line:LoginChannelId`
- ค่าลับ (`Line:ChannelSecret`, `Line:ChannelAccessToken`, `ConnectionStrings:Default`, `Jobs:Key`) ใส่ใน `appsettings.Production.json` บนเซิร์ฟเวอร์ หรือ environment variable ใน `web.config` — ห้าม commit ขึ้น git
- MySQL: สร้างฐานข้อมูลและ user ที่ Plesk → Databases; connection string ใช้ `utf8mb4`; แอปเก็บเวลาเป็น UTC และแปลงเป็นเวลาไทยเฉพาะตอนแสดงผล
- การเตือน: Plesk → Scheduled Tasks → "Fetch a URL" `https://api.drivetodev.online/jobs/reminders` **ทุกชั่วโมง** (job จะส่งหลัง 10:00 เวลาไทยเท่านั้น และส่งได้วันละครั้งต่อบิล — ดูข้อ 4.5) **ห้าม**พึ่ง timer ใน `BackgroundService` เพราะ IIS อาจหยุดหรือ recycle app pool
- การรับ webhook: ตอบ `200` ให้เร็ว event ปกติประมวลผลทันที งานที่ช้า (ตรวจสลิป) ส่งเข้า worker แบบ `Channel<T>` ในหน่วยความจำ ถ้าแอป recycle กลางคัน job ทวงเงินจะหยิบ payment ที่ค้างสถานะ `SUBMITTED` และยังไม่ถูกตรวจมาทำต่อ
- Logging: ใช้ Serilog เขียนไฟล์แบบ rolling ไว้ที่ `logs/` (เปิดดูได้จาก File Manager ของ Plesk) และปิด `stdoutLogEnabled` บน production
- โฟลเดอร์ `App_Data/` ต้องให้ app-pool identity เขียนได้ และต้องไม่ถูกเขียนทับตอน deploy

## 11. Roadmap

1. **MVP**: ลงทะเบียนสมาชิกกลุ่มอัตโนมัติ, สร้างบิลกลุ่มและรายคน (หารเท่า), รวมยอดที่เพิ่มเข้าบิลที่เปิดอยู่, หน้า LIFF จ่ายเงิน (QR พร้อมเพย์ + แนบสลิป), ยืนยันสลิปด้วยมือ, คำสั่ง `ยอด`, เตือนวันละครั้ง 10:00 (สรุปในกลุ่ม / ส่วนตัว)
2. หารแบบกำหนดรายคน, รหัสเชิญ, rich menu, "ทวงตอนนี้"
3. ตรวจสลิปอัตโนมัติ, แดชบอร์ดบิล/ประวัติบน LIFF, export (CSV)
