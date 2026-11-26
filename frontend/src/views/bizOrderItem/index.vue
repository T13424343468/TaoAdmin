<!-- ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件） ====
     订单明细 列表页：配置驱动（字段/字典/查询/导入导出按运行时配置渲染，补配即生效） -->
<template>
  <div class="page-container">
    <!-- 搜索与操作区 -->
    <div class="table-operator">
      <a-input v-model:value="keyword" placeholder="搜索" allow-clear style="width: 220px" @press-enter="loadData()" />
      <template v-for="f in queryFields" :key="f.name">
        <a-select v-if="ctrlKind(f) === 'dict'" v-model:value="queryState[f.name]" :options="dictOptions[f.dictCode] || []" :placeholder="f.label" allow-clear style="width: 160px; margin-left: 8px" />
        <a-input-number v-else-if="f.queryType === 'eq' && ['int', 'long', 'decimal'].includes(f.dataType)" v-model:value="queryState[f.name]" :placeholder="f.label" style="width: 160px; margin-left: 8px" />
        <a-date-picker v-else-if="f.queryType === 'eq' && f.dataType === 'datetime'" v-model:value="queryState[f.name]" show-time value-format="YYYY-MM-DD HH:mm:ss" :placeholder="f.label" style="width: 200px; margin-left: 8px" />
        <a-input v-else-if="f.queryType === 'eq'" v-model:value="queryState[f.name]" :placeholder="f.label" allow-clear style="width: 160px; margin-left: 8px" />
        <template v-else-if="f.queryType === 'range' && ['int', 'long', 'decimal'].includes(f.dataType)">
          <a-input-number v-model:value="queryState[f.name + '_from']" :placeholder="f.label + '从'" style="width: 120px; margin-left: 8px" />
          <a-input-number v-model:value="queryState[f.name + '_to']" :placeholder="f.label + '到'" style="width: 120px" />
        </template>
        <template v-else-if="f.queryType === 'range' && f.dataType === 'datetime'">
          <a-date-picker v-model:value="queryState[f.name + '_from']" value-format="YYYY-MM-DD" :placeholder="f.label + '从'" style="width: 130px; margin-left: 8px" />
          <a-date-picker v-model:value="queryState[f.name + '_to']" value-format="YYYY-MM-DD" :placeholder="f.label + '到'" style="width: 130px" />
        </template>
      </template>
      <a-button type="primary" style="margin-left: 8px" @click="loadData()">查询</a-button>
      <a-button style="margin-left: 8px" @click="resetQuery">重置</a-button>
      <a-button type="primary" style="margin-left: 12px" @click="openCreate">新增</a-button>
      <a-space v-if="selectedRowKeys.length > 0" style="margin-left: 12px">
        <span>已选 {{ selectedRowKeys.length }} 项</span>
        <a-button danger @click="onBatchDelete">批量删除</a-button>
      </a-space>
    </div>

    <!-- 表格（列按运行时配置渲染） -->
    <a-table
      :data-source="rows"
      :columns="listColumns"
      :loading="loading"
      row-key="id"
      size="middle"
      :pagination="pagination"
      :row-selection="{ selectedRowKeys, onChange: (k: any[]) => (selectedRowKeys = k) }"
      @change="onTableChange"
    >
      <template #bodyCell="{ column, record }">
        <template v-if="column.key === 'action'">
          <a-space>
            <a @click="openEdit(record)">编辑</a>
            <a-popconfirm title="确定删除该条记录吗？" @confirm="onDelete(record)">
              <a class="danger-link">删除</a>
            </a-popconfirm>
          </a-space>
        </template>
        <template v-else-if="dictMap[column.key]">
          {{ dictLabel(dictMap[column.key], record[column.key]) }}
        </template>
        <template v-else-if="boolKeys.includes(column.key)">
          {{ record[column.key] ? '是' : '否' }}
        </template>
      </template>
    </a-table>

    <!-- 新增/编辑弹窗（控件按运行时配置渲染） -->
    <a-modal v-model:open="dialogVisible" :title="form.id ? '编辑' : '新增'" :confirm-loading="saving" @ok="onSave">
      <a-form layout="vertical">
        <a-form-item v-for="f in formFields" :key="f.name" :label="f.label" :name="jsKey(f.name)" :required="!f.nullable">
          <a-input-number v-if="ctrlKind(f) === 'number'" v-model:value="form[jsKey(f.name)]" style="width: 100%" />
          <a-date-picker v-else-if="ctrlKind(f) === 'date'" v-model:value="form[jsKey(f.name)]" show-time value-format="YYYY-MM-DD HH:mm:ss" style="width: 100%" />
          <a-textarea v-else-if="ctrlKind(f) === 'textarea'" v-model:value="form[jsKey(f.name)]" :rows="3" />
          <a-select v-else-if="ctrlKind(f) === 'dict'" v-model:value="form[jsKey(f.name)]" :options="dictOptions[f.dictCode] || []" placeholder="请选择" allow-clear />
          <a-switch v-else-if="ctrlKind(f) === 'switch'" v-model:checked="form[jsKey(f.name)]" />
          <a-input v-else v-model:value="form[jsKey(f.name)]" />
        </a-form-item>
      </a-form>
    </a-modal>
  </div>
</template>

<script setup lang="ts">
import { pageBizOrderItem, getBizOrderItem, saveBizOrderItem, deleteBizOrderItem, batchDeleteBizOrderItem, getBizOrderItemConfig } from '@/api/bizOrderItem'
import { request } from '@/api/request'
import { message, Modal } from 'ant-design-vue'
import { ref, reactive, computed } from 'vue'

// 运行时配置：字段/字典/查询/导入导出按配置渲染，增强配置补配即生效
const config = ref<any>({ fields: [], enhancement: {} })
const fields = computed<any[]>(() => config.value.fields || [])
const enhancement = computed<any>(() => config.value.enhancement || {})
// 设计字段名（snake_case）→ 接口 JSON 键（camelCase，与后端序列化口径一致）
function jsKey(name: string): string {
  return name.replace(/_([a-z0-9])/g, (_: string, c: string) => c.toUpperCase())
}
const listColumns = computed(() => [
  ...fields.value.filter((f: any) => f.showInList).map((f: any) => ({ title: f.label, dataIndex: jsKey(f.name), key: jsKey(f.name) })),
  { title: '操作', key: 'action', width: 140 },
])
const formFields = computed(() => fields.value)
const boolKeys = computed(() => fields.value.filter((f: any) => f.dataType === 'bool').map((f: any) => jsKey(f.name)))
const dictMap = computed<Record<string, string>>(() => {
  const m: Record<string, string> = {};
  fields.value.filter((f: any) => f.control === 'dict' && f.dictCode).forEach((f: any) => (m[jsKey(f.name)] = f.dictCode));
  return m;
})
const dictOptions = ref<Record<string, any[]>>({})
function ctrlKind(f: any): string {
  if (f.control === 'dict' && f.dictCode) return 'dict'
  if (f.control === 'number' || ['int', 'long', 'decimal'].includes(f.dataType)) return 'number'
  if (f.control === 'date' || f.dataType === 'datetime') return 'date'
  if (f.control === 'textarea') return 'textarea'
  if (f.control === 'switch') return 'switch'
  return 'text'
}
function dictLabel(code: string, value: any): string {
  const hit = (dictOptions.value[code] || []).find((o: any) => String(o.value) === String(value))
  return hit ? hit.label : (value ?? '')
}

const rows = ref<any[]>([])
const loading = ref(false)
const saving = ref(false)
const keyword = ref('')
const queryFields = computed(() => fields.value.filter((f: any) => f.queryType && f.queryType !== 'none'))
const queryState = reactive<Record<string, any>>({})
const selectedRowKeys = ref<any[]>([])
const dialogVisible = ref(false)
const pagination = reactive({ current: 1, pageSize: 10, total: 0, showSizeChanger: true, showTotal: (t: number) => `共 ${t} 条` })
const form = ref<any>({ id: undefined })

async function loadConfig() {
  try {
    config.value = await getBizOrderItemConfig()
  } catch (e: any) {
    // 配置加载失败时兜底为空配置，页面仍可渲染，避免整页空白
    config.value = { fields: [], details: [] } as any
    message.error('页面配置加载失败：' + (e?.message || '未知错误'))
    throw e
  }
  for (const code of Object.values(dictMap.value)) {
    if (!dictOptions.value[code]) {
      try {
        dictOptions.value[code] = await request({ url: `/identity/dict/items/${code}`, method: 'get' })
      } catch {
        dictOptions.value[code] = []
      }
    }
  }
}

async function loadData() {
  loading.value = true
  try {
    const qp: Record<string, string> = {}
    for (const f of queryFields.value) {
      if (f.queryType === 'eq') {
        const v = queryState[f.name]
        if (v !== undefined && v !== null && v !== '') qp[f.name] = String(v)
      } else if (f.queryType === 'range') {
        const from = queryState[f.name + '_from']
        const to = queryState[f.name + '_to']
        if (from !== undefined && from !== null && from !== '') qp[f.name + '_from'] = String(from)
        if (to !== undefined && to !== null && to !== '') qp[f.name + '_to'] = f.dataType === 'datetime' && String(to).length === 10 ? String(to) + ' 23:59:59' : String(to)
      }
    }
    const res = await pageBizOrderItem({ pageIndex: pagination.current, pageSize: pagination.pageSize, keyword: keyword.value || undefined, ...qp })
    rows.value = res.items
    pagination.total = res.total
  } finally {
    loading.value = false
  }
}

function resetQuery() {
  keyword.value = ''
  Object.keys(queryState).forEach((k) => delete queryState[k])
  pagination.current = 1
  loadData()
}

function onTableChange(p: any) {
  pagination.current = p.current
  pagination.pageSize = p.pageSize
  loadData()
}

function openCreate() {
  form.value = { id: undefined }
  dialogVisible.value = true
}

async function openEdit(row: any) {
  form.value = await getBizOrderItem(row.id)
  dialogVisible.value = true
}

async function onSave() {
  saving.value = true
  try {
    await saveBizOrderItem(form.value)
    message.success('保存成功')
    dialogVisible.value = false
    await loadData()
  } finally {
    saving.value = false
  }
}

function onDelete(row: any) {
  return deleteBizOrderItem(row.id).then(() => {
    message.success('删除成功')
    return loadData()
  })
}

function onBatchDelete() {
  Modal.confirm({
    title: '批量删除',
    content: `确认删除选中的 ${selectedRowKeys.value.length} 条记录？`,
    onOk: async () => {
      const res = await batchDeleteBizOrderItem(selectedRowKeys.value)
      message.success(`已删除 ${res.deleted} 条`)
      selectedRowKeys.value = []
      await loadData()
    }
  })
}

loadConfig().then(loadData).catch(() => {})
</script>
