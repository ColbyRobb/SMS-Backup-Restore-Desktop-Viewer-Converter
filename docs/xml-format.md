# SMS Backup & Restore — XML format reference

A working reference for the *SMS Backup & Restore* XML format, building on SyncTech's published
[field documentation](https://www.synctech.com.au/sms-backup-restore/fields-in-xml-backup-files/)
with the additional behaviour a reader needs to get right.

## File kinds

Backups are written as two separate file kinds, named with the local time the backup was taken:

| Name | Root | Contains |
|---|---|---|
| `sms-YYYYMMDDHHMMSS.xml` | `<smses>` | **Both `<sms>` and `<mms>` records** |
| `calls-YYYYMMDDHHMMSS.xml` | `<calls>` | `<call>` records |

MMS is *not* a separate file — messages and multimedia messages share the `<smses>` root and are
distinguished by element name. So there are two file kinds but three record types.

The filename timestamp is worth reading: it is the only record of when a backup was taken for files
whose root element lacks `backup_date`. The root element remains authoritative for the file's kind,
since a file can be renamed; a mismatch between name and root is worth flagging but not obeying.

A user with years of history will have many files of both kinds, and **their date ranges overlap
heavily** — each backup typically covers everything on the device, not only what is new. Importing
a folder therefore means merging and de-duplicating, not concatenating.

Both roots carry `count` — a record total best treated as a *hint for progress reporting*, since a
reader cannot rely on it matching the records actually present. Use it to size a progress bar, and
count records as you go for anything that must be accurate.

## `<smses>` / `<calls>` root

| Attribute | Meaning |
|---|---|
| `count` | Number of records claimed. Advisory. |
| `backup_set` | GUID identifying the backup set (present on newer versions). |
| `backup_date` | Java epoch milliseconds when the backup was taken. |
| `type` | `full` or `incremental` on newer versions. |

## `<sms>`

A complete SMS record. All content is in attributes; the element has no children.

| Attribute | Meaning | Values |
|---|---|---|
| `protocol` | Message protocol | Usually `0` |
| `address` | Sender/recipient number | Raw, formatting varies wildly |
| `date` | Timestamp | Java epoch **milliseconds** |
| `type` | Direction/state | 1=Received 2=Sent 3=Draft 4=Outbox 5=Failed 6=Queued |
| `subject` | Subject | Effectively always `null` for SMS |
| `body` | Message text | May contain any Unicode incl. emoji |
| `toa` / `sc_toa` | Type of address | Not applicable, usually `null` |
| `service_center` | SMSC | `null` for sent messages |
| `read` | Read state | 1=Read 0=Unread |
| `status` | Delivery status | -1=None 0=Complete 32=Pending 64=Failed |
| `sub_id` | SIM subscription | `0`, `1`, `2`, … ; `-1` when unknown |
| `readable_date` | Human-readable timestamp | Optional, locale-formatted at backup time |
| `contact_name` | Contact name at backup time | Optional; `(Unknown)` when unresolved |

## `<mms>`

Metadata in attributes; content in child `<parts>` and `<addrs>`.

| Attribute | Meaning | Values |
|---|---|---|
| `address` | Number, or `~`-joined list for groups | See grouping rules below |
| `date` | Timestamp | Java epoch milliseconds |
| `date_sent` | Sent timestamp | Often `0` |
| `msg_box` | Direction | 1=Received 2=Sent 3=Draft 4=Outbox |
| `m_type` | MMS spec message type | **134 = delivery receipt** — has no readable body, render as such |
| `ct_t` | Content-Type | Usually `application/vnd.wap.multipart.related` |
| `read`, `sub_id`, `readable_date`, `contact_name` | As per SMS | |

### `<mms><parts><part>`

| Attribute | Meaning |
|---|---|
| `seq` | Ordering of the part. `-1` marks the SMIL layout part. |
| `ct` | MIME type — the dispatch key |
| `name` | Part name |
| `cl` | Content location; the closest thing to a filename |
| `chset` | Charset of a text part |
| `text` | Text content, present when `ct="text/plain"` |
| `data` | **Base64-encoded binary payload** for any non-text part |

Dispatch by `ct`:
- `application/smil` — layout metadata, skip entirely.
- `text/plain` — take `text`. Multiple text parts concatenate in `seq` order.
- anything else — decode `data` from base64. Images/audio/video render inline; everything
  else (vCards, PDFs, `application/octet-stream`) is an attachment to extract by `cl`.

`data` is the reason streaming matters: a single attribute value can be tens of megabytes
of base64 on one line. It must never be materialized casually.

### `<mms><addrs><addr>`

| Attribute | Meaning | Values |
|---|---|---|
| `address` | Participant number | `insert-address-token` = the device owner, exclude it |
| `type` | Role | 129=BCC 130=CC 151=To **137=From** |
| `charset` | Charset | |

## `<call>`

| Attribute | Meaning | Values |
|---|---|---|
| `number` | Phone number | |
| `duration` | Length in **seconds** | `0` for missed/rejected |
| `date` | Timestamp | Java epoch milliseconds |
| `type` | Category | 1=Incoming 2=Outgoing 3=Missed 4=Voicemail 5=Rejected 6=Refused/Blocked |
| `presentation` | Caller ID state | 1=Allowed 2=Restricted 3=Unknown 4=Payphone |
| `subscription_id` | SIM subscription | Optional |
| `readable_date`, `contact_name` | As per SMS | |

## Behavioral rules

These go beyond the field list, and are required for output to be *correct* rather than merely
plausible.

Credit for them belongs to SyncTech: the rules below were learned from the JavaScript behind
their own web viewer, which they publish unminified and pointed to specifically so that others
could read it and build their own tools.

**1. Conversation grouping — last 8 digits.** `fixNumber()` strips space, `-`, `(` and `)`,
then keeps only the trailing 8 characters (`NUMBER_COMPARISION_LENGTH`). Two addresses match
if these agree. This is what makes `+1 555-867-5309` and `5558675309` one thread. It also means
short codes (< 8 digits) compare in full.

**2. Group threads — sorted `~`-joined addresses.** A group MMS carries multiple `<addr>`
elements. The participant list must be **sorted** before joining with `~`, because the elements
appear in inconsistent order across messages; without sorting, one conversation fragments into
several. `insert-address-token` is excluded. An address already containing `~` is never
re-normalized through `fixNumber`.

**3. RCS placeholder addresses.** Google Messages stores some RCS threads with an `address`
ending in `@rcs.google.com` instead of real participants. When seen, the real address list must
be read from the child `<addr>` elements, and for received messages (`msg_box=1`) the sender is
the `type="137"` addr. Note the ordering constraint: reading `<addr>` children advances a pull
parser past the message, so **all `<mms>` attributes must be read before descending into children**.

**4. Received group messages.** When `address` contains `~` and `msg_box=1`, the individual
sender is the `type="137"` addr — otherwise you cannot tell who in the group spoke.

**5. Attribute presence varies by phone.** SyncTech's documentation helpfully notes that "the actual
attributes vary depending on the phone." Every attribute must be treated as optional. This is
why the reader captures *all* attributes rather than a fixed subset, keeping anything it does
not interpret so a future format change cannot silently drop data.
