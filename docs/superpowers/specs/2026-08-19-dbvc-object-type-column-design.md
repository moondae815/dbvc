# 객체 유형(Object Type) 컬럼 추가 설계

## 1. 개요
현재 DBVC의 'View Changes' 도구 창에서 제공하는 변경 목록(스테이징, 상태, 객체)에 사용자가 객체의 종류를 직관적으로 파악할 수 있도록 **객체 유형(Object Type)** 컬럼을 추가합니다. 데이터베이스에서 반환하는 원시 타입을 사용하기 편한 약어로 매핑하여 가독성을 높입니다.

## 2. 변경 내용

### 2.1. UI 뷰모델 확장 (`ChangeItemViewModel.cs`)
* **`ObjectType` 프로퍼티 추가**: `ChangeRecord`에서 넘겨받은 원본 객체 타입(예: `PROCEDURE`, `TABLE`)을 보관할 프로퍼티입니다.
* **`ObjectTypeText` 프로퍼티 추가**: 원본 타입을 화면에 보여주기 위해 친화적인 텍스트로 변환하는 읽기 전용 프로퍼티입니다.
  * **매핑 규칙**:
    * `PROCEDURE` → `SP`
    * `FUNCTION` → `UDF`
    * `TABLE` → `Table`
    * `VIEW` → `View`
    * `TRIGGER` → `Trigger`
  * 매핑되지 않은 값(기타 타입)은 원본 데이터의 첫 글자를 대문자로 변환하거나 그대로 출력하여 누락 없이 표시합니다.

### 2.2. 데이터 바인딩 로직 수정 (`ViewChangesViewModel.cs`)
* 변경 목록을 새로고침하여 UI 객체를 생성하는 `ApplyRefreshOutcome` 내의 매핑 코드에 `ObjectType` 할당 로직을 추가합니다.
  * `ChangeItemViewModel` 생성 시 `ObjectType = record.ObjectType` 대입.

### 2.3. UI 프레젠테이션 뷰 수정 (`ViewChangesControl.xaml`)
* `ListView` 내의 `GridView` 컬럼 정의에서 "객체" 컬럼 다음에 새로운 `GridViewColumn`을 추가합니다.
* **설정값**:
  * `Header="객체 유형"`
  * `DisplayMemberBinding="{Binding ObjectTypeText}"`

## 3. 영향도 및 제약 사항
* 기존 데이터 계층(`ChangeLog` 테이블 및 `ChangeRecord` 모델)에는 이미 `ObjectType`이 수집되고 있으므로 DB 스키마나 DDL 트리거 수정은 필요하지 않습니다.
* UI 단의 가벼운 추가이므로 성능 저하나 기존 Git/SMO 로직에 미치는 부작용이 없습니다.
