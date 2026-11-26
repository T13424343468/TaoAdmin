<template>
  <PageLayout>
    <!-- 筛选区（全站统一 FilterBar：字段在左，查询/重置在右） -->
    <FilterBar :values="filters" :fields="filterFields" :loading="loading" @search="reload" @reset="resetQuery">
      <template #extra>
        <a @click="goFullAudit">查看全部审计日志</a>
      </template>
    </FilterBar>

    <!-- 操作区（全站统一 table-operator：导出在左，批量与选中全部在右） -->
    <div class="table-operator">
      <a-button :loading="exporting" @click="onExport">导出 Excel</a-button>
      <a-space v-if="selectedCount > 0" style="margin-left: 12px">
        <span class="batch-count">已选 {{ selectedCount }} 项</span>
        <a-button danger :loading="batchLoading" @click="onBatchDelete">批量删除</a-button>
        <a @click="clearSelection">清空勾选</a>
      </a-space>
      <a-button v-else-if="total > 0" style="margin-left: 12px" @click="onSelectAll">选中全部 {{ total }} 条</a-button>
    </div>

    <template #table="{ scrollY }">
    <a-table
      :scroll="{ y: scrollY }"
      :columns="columns"
      :data-source="rows"
      :loading="loading"
      :pagination="{
        current: query.pageIndex,
        pageSize: query.pageSize,
        total,
        showSizeChanger: true,
        onChange: (p: number, s: number) => { query.pageIndex = p; query.pageSize = s; void load(); },
      }"
      row-key="id"
      size="small"
      :row-selection="{ selectedRowKeys, onChange: onTableSelectChange }"
    >
      <template #bodyCell="{ column, record }">
        <template v-if="column.key === 'success'">
          <a-tag :color="record.success ? 'green' : 'red'">{{ record.success ? '成功' : '失败' }}</a-tag>
        </template>
      </template>
    </a-table>
    </template>
  </PageLayout>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import PageLayout from '@/components/PageLayout/index.vue';
import { useRouter } from 'vue-router';
import { message, Modal } from 'ant-design-vue';
import FilterBar, { type FilterField } from '@/components/FilterBar/index.vue';
import { useBatchSelection } from '@/composables/useBatchSelection';
import { pageAfterDelete } from '@/utils/listPage';
import { getAuditLogs, cleanupAuditLogs, batchDeleteAuditLogs, exportAuditLogs, type AuditLogItem } from '@/api/audit';
import type { Dayjs } from 'dayjs';

// ⑰ 登录日志筛选视图：数据与审计日志同源（category=login），本页固定类别、隐藏类别筛选
const router = useRouter();

const columns = [
  { title: '时间', dataIndex: 'createdAt', key: 'createdAt', width: 170 },
  { title: '账号', dataIndex: 'userName', key: 'userName', width: 120 },
  { title: '动作', dataIndex: 'action', key: 'action' },
  { title: '结果', key: 'success', width: 80 },
  { title: 'IP', dataIndex: 'ip', key: 'ip', width: 140 },
];

const rows = ref<AuditLogItem[]>([]);
const total = ref(0);
const loading = ref(false);

// ============ 筛选区（统一 FilterBar 口径） ============

const filters = reactive({
  userName: undefined as string | undefined,
  success: undefined as number | undefined,
  timeRange: undefined as [Dayjs, Dayjs] | undefined,
});

const filterFields: FilterField[] = [
  { key: 'userName', label: '账号', type: 'input', primary: true, width: 140 },
  {
    key: 'success', label: '结果', type: 'select', primary: true, width: 110,
    options: [{ label: '成功', value: 1 }, { label: '失败', value: 0 }],
  },
  { key: 'timeRange', label: '时间区间', type: 'dateRange', primary: true },
];

function resetQuery() {
  filters.userName = undefined;
  filters.success = undefined;
  filters.timeRange = undefined;
  reload();
}

// ============ 批量选择（跨页保留） ============

const batchLoading = ref(false);
const {
  selectedRowKeys,
  selectedCount,
  onTableSelectChange,
  setTotal,
  selectAll,
  clearSelection,
  getSelectedIds,
} = useBatchSelection(async () => rows.value.map((r) => r.id));

async function onSelectAll() {
  await selectAll();
  message.info(`已按当前筛选条件选中全部 ${selectedCount.value} 条`);
}

const query = reactive({
  pageIndex: 1,
  pageSize: 20,
});

// 游标缓存：记录每一页返回的下一页游标，翻下一页时带上可恒定速度定位（亿级深翻页不衰减）
const cursorMap = new Map<number, string>();

function rangeParams() {
  const r = filters.timeRange;
  return r && r[0] && r[1]
    ? { begin: r[0].format('YYYY-MM-DD HH:mm:ss'), end: r[1].format('YYYY-MM-DD HH:mm:ss') }
    : {};
}

async function load() {
  loading.value = true;
  try {
    // 上一页的游标存在时走游标翻页（顺序翻页场景）；跳页/首次加载回退 OFFSET
    const prevCursor = cursorMap.get(query.pageIndex - 1);
    const page = await getAuditLogs({
      ...query,
      category: 'login',
      userName: filters.userName,
      success: filters.success,
      ...rangeParams(),
      ...(prevCursor ? { cursor: prevCursor } : {}),
    });
    rows.value = page.items;
    total.value = page.total;
    setTotal(page.total);
    if (page.nextCursor) cursorMap.set(query.pageIndex, page.nextCursor);
    else cursorMap.delete(query.pageIndex);
  } finally {
    loading.value = false;
  }
}

function reload() {
  query.pageIndex = 1;
  cursorMap.clear();
  clearSelection();
  void load();
}

function onBatchDelete() {
  const ids = getSelectedIds();
  if (!ids.length) return;
  Modal.confirm({
    title: '批量删除',
    content: `确定删除选中的 ${ids.length} 条登录日志吗？删除后不可恢复。`,
    okText: '删除',
    okType: 'danger',
    async onOk() {
      batchLoading.value = true;
      try {
        const r = await batchDeleteAuditLogs(ids);
        message.success(`批量删除完成：成功 ${r.deleted} 条`);
        clearSelection();
        query.pageIndex = pageAfterDelete(total.value, r.deleted, query.pageIndex, query.pageSize);
        cursorMap.clear();
        await load();
      } finally {
        batchLoading.value = false;
      }
    },
  });
}

async function onCleanup() {
  await cleanupAuditLogs();
}

// ============ 导出 Excel：按当前筛选条件，后端上限 5 万条 ============

const exporting = ref(false);

async function onExport() {
  exporting.value = true;
  try {
    const blob = await exportAuditLogs({
      category: 'login',
      userName: filters.userName,
      success: filters.success,
      ...rangeParams(),
    });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `login-log-${new Date().toISOString().slice(0, 19).replace(/[-:T]/g, '')}.xlsx`;
    a.click();
    URL.revokeObjectURL(url);
    message.success('导出完成，已按当前筛选条件生成');
  } finally {
    exporting.value = false;
  }
}

function goFullAudit() {
  void router.push('/system/auditlog');
}

onMounted(load);
</script>

<style scoped>
.batch-count {
  color: #1677ff;
}
</style>
