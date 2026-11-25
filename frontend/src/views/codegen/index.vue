<template>
  <PageLayout>
    <a-tabs v-model:active-key="activeTab">
      <!-- ============ 表设计 ============ -->
      <a-tab-pane key="designs" title="表设计">
        <!-- 筛选区（全站统一 FilterBar：字段在左，查询/重置在右） -->
        <FilterBar :values="designFilters" :fields="designFilterFields" :loading="designLoading" @search="loadDesigns()" @reset="resetDesignQuery" />

        <!-- 操作区（全站统一 table-operator） -->
        <div class="table-operator">
          <a-button v-permission="'sys:codegen:manage'" type="primary" @click="openCreate">
            <template #icon><PlusOutlined /></template>
            新建表设计
          </a-button>
        </div>


      </a-tab-pane>

      <!-- ============ 生成记录 ============ -->
      <a-tab-pane key="records" title="生成记录" />

      <!-- ============ 增强配置（⑲补配即生效） ============ -->
      <a-tab-pane key="enhance" title="增强配置">
        <div class="table-operator">
          <a-select
            v-model:value="enhDesignId"
            placeholder="选择要补配的模块"
            style="width: 340px"
            :options="enhOptions"
            show-search
            option-filter-prop="label"
            @change="onEnhSelect"
          />
        </div>
        <template v-if="enhDetail">
          <a-alert
            type="info"
            show-icon
            style="margin-bottom: 12px"
            message="补配即生效：生成页运行时读取本配置渲染字段/字典/查询与导入导出按钮，无需重新生成；若该模块此前已生成且未带导入导出后端接口，重新生成一次即可补齐。"
          />
          <a-form layout="inline" style="margin-bottom: 12px">
            <a-form-item label="Excel 导出">
              <a-switch v-model:checked="enhExport" />
            </a-form-item>
            <a-form-item label="Excel 导入（模板下载 + 逐行校验）">
              <a-switch v-model:checked="enhImport" />
            </a-form-item>
          </a-form>
          <a-table
            :data-source="enhFields"
            :columns="enhColumns"
            :pagination="false"
            row-key="_key"
            size="small"
            :scroll="{ x: 720 }"
          >
            <template #bodyCell="{ column, record }">
              <template v-if="column.key === 'control'">
                {{ controlLabel(record.control) }}
              </template>
              <template v-else-if="column.key === 'dictCode'">
                <a-input v-if="record.control === 'dict'" v-model:value="record.dictCode" placeholder="字典编码" size="small" />
                <span v-else>-</span>
              </template>
              <template v-else-if="column.key === 'queryType'">
                <a-select v-model:value="record.queryType" style="width: 90px" size="small">
                  <a-select-option value="none">不查询</a-select-option>
                  <a-select-option value="eq">精确</a-select-option>
                  <a-select-option value="like">模糊</a-select-option>
                  <a-select-option value="range">范围</a-select-option>
                </a-select>
              </template>
            </template>
          </a-table>
          <a-button type="primary" style="margin-top: 12px" :loading="enhSaving" @click="onEnhSave">保存增强配置</a-button>
        </template>
      </a-tab-pane>

      <!-- ============ 服务管理（③④：服务清单 + 一键生成新服务） ============ -->
      <a-tab-pane key="services" title="服务管理">
        <div class="table-operator">
          <a-button v-permission="'sys:codegen:manage'" type="primary" @click="openScaffold">
            <template #icon><PlusOutlined /></template>
            生成新服务
          </a-button>
        </div>
        <a-table
          :data-source="services"
          :columns="serviceColumns"
          :loading="servicesLoading"
          row-key="id"
          size="middle"
          :pagination="false"
        >
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'isBuiltIn'">
              <a-tag v-if="record.isBuiltIn" color="blue">内置</a-tag>
              <a-tag v-else color="green">新增</a-tag>
            </template>
          </template>
        </a-table>
      </a-tab-pane>
    </a-tabs>

    <template #table="{ scrollY }">
        <a-table
          v-show="activeTab === 'designs'"
          :data-source="designs"
          :columns="designColumns"
          :loading="designLoading"
          row-key="id"
          size="middle"
          :pagination="designPagination"
          :scroll="{ x: 900, y: scrollY }"
          @change="onDesignTableChange"
        >
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'tableName'">
              <a @click="openEdit(record)">{{ record.tableName }}</a>
            </template>
            <template v-else-if="column.key === 'fieldCount'">
              {{ parseFieldCount(record.fieldsJson) }}
            </template>
            <template v-else-if="column.key === 'mdType'">
              <a-tag v-if="record.isMasterDetail" color="purple">主从</a-tag>
              <a-tag v-else>单表</a-tag>
            </template>
            <template v-else-if="column.key === 'lastSyncAt'">
              <a-tag v-if="record.lastSyncAt" color="green">已同步</a-tag>
              <a-tag v-else color="orange">未同步</a-tag>
            </template>
            <template v-else-if="column.key === 'action'">
              <a-space>
                <a v-permission="'sys:codegen:manage'" @click="openEdit(record)">设计</a>
                <a-popconfirm
                  v-permission="'sys:codegen:manage'"
                  title="同步会按设计建表/加列（自动执行），确定继续吗？"
                  @confirm="onSync(record)"
                >
                  <a v-permission="'sys:codegen:manage'">同步建库</a>
                </a-popconfirm>
                <a-popconfirm
                  v-permission="'sys:codegen:manage'"
                  title="一键生成全套增删改查前后端代码，确定继续吗？"
                  @confirm="onGenerate(record)"
                >
                  <a v-permission="'sys:codegen:manage'">一键生成</a>
                </a-popconfirm>
                <a-popconfirm
                  v-permission="'sys:codegen:manage'"
                  title="删除表设计不影响已生成的库表与代码，确定删除吗？"
                  @confirm="onDeleteDesign(record)"
                >
                  <a class="danger-link" v-permission="'sys:codegen:manage'">删除</a>
                </a-popconfirm>
              </a-space>
            </template>
          </template>
        </a-table>

        <a-table
          v-show="activeTab === 'records'"
          :data-source="records"
          :columns="recordColumns"
          :loading="recordLoading"
          row-key="id"
          size="middle"
          :pagination="recordPagination"
          :scroll="{ x: 800, y: scrollY }"
          @change="onRecordTableChange"
        >
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'mode'">
              <a-tag>{{ record.mode === 'generate' ? '生成' : record.mode }}</a-tag>
            </template>
            <template v-else-if="column.key === 'elapsedMs'">
              {{ record.elapsedMs }} ms
            </template>
            <template v-else-if="column.key === 'action'">
              <a @click="openRecordDetail(record)">查看文件</a>
            </template>
          </template>
        </a-table>
    </template>

    <!-- ============ 表设计编辑弹窗 ============ -->
    <a-modal
      v-model:open="editVisible"
      :title="editForm.id ? '表设计：' + editForm.tableName : '新建表设计'"
      width="960px"
      :confirm-loading="saving"
      ok-text="保存设计"
      @ok="onSaveDesign"
    >
      <a-form :model="editForm" layout="vertical">
        <a-row :gutter="16">
          <a-col :span="8">
            <a-form-item label="表名（英文下划线风格）" name="tableName" required>
              <a-input v-model:value="editForm.tableName" :disabled="!!editForm.id" placeholder="如 biz_contract" />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="中文名（业务名）" name="displayName" required>
              <a-input v-model:value="editForm.displayName" placeholder="如 合同管理" />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="备注" name="remark">
              <a-input v-model:value="editForm.remark" placeholder="选填" />
            </a-form-item>
          </a-col>
        </a-row>
        <a-row :gutter="16">
          <a-col :span="8">
            <a-form-item label="目标服务（控制器落进哪个服务）" name="targetService">
              <a-select
                v-model:value="editForm.targetService"
                placeholder="默认：权限服务"
                allow-clear
                :options="serviceOptions"
              />
            </a-form-item>
          </a-col>
        </a-row>

        <!-- 字段设计编辑表 -->
        <a-form-item label="字段设计" required>
          <a-table
            :data-source="editForm.fields"
            :columns="fieldColumns"
            :pagination="false"
            row-key="_key"
            size="small"
            :scroll="{ x: 1080 }"
          >
            <template #bodyCell="{ column, record, index }">
              <template v-if="column.key === 'name'">
                <a-input v-model:value="record.name" placeholder="英文" :disabled="!!editForm.id" />
              </template>
              <template v-else-if="column.key === 'label'">
                <a-input v-model:value="record.label" placeholder="中文" />
              </template>
              <template v-else-if="column.key === 'dataType'">
                <a-select v-model:value="record.dataType" style="width: 110px">
                  <a-select-option value="string">string</a-select-option>
                  <a-select-option value="int">int</a-select-option>
                  <a-select-option value="long">long</a-select-option>
                  <a-select-option value="decimal">decimal</a-select-option>
                  <a-select-option value="datetime">datetime</a-select-option>
                  <a-select-option value="bool">bool</a-select-option>
                  <a-select-option value="text">text</a-select-option>
                </a-select>
              </template>
              <template v-else-if="column.key === 'length'">
                <a-input-number
                  v-if="record.dataType === 'string' || record.dataType === 'decimal'"
                  v-model:value="record.length"
                  :min="1"
                  :max="record.dataType === 'decimal' ? 38 : 8000"
                  size="small"
                  style="width: 80px"
                />
                <span v-else>-</span>
              </template>
              <template v-else-if="column.key === 'precision'">
                <a-input-number
                  v-if="record.dataType === 'decimal'"
                  v-model:value="record.precision"
                  :min="0"
                  :max="10"
                  size="small"
                  style="width: 70px"
                />
                <span v-else>-</span>
              </template>
              <template v-else-if="column.key === 'nullable'">
                <a-checkbox v-model:checked="record.nullable" />
              </template>
              <template v-else-if="column.key === 'showInList'">
                <a-checkbox v-model:checked="record.showInList" />
              </template>
              <template v-else-if="column.key === 'control'">
                <a-select v-model:value="record.control" style="width: 100px">
                  <a-select-option value="text">文本</a-select-option>
                  <a-select-option value="number">数字</a-select-option>
                  <a-select-option value="date">日期</a-select-option>
                  <a-select-option value="textarea">多行</a-select-option>
                  <a-select-option value="dict">字典</a-select-option>
                  <a-select-option value="switch">开关</a-select-option>
                </a-select>
              </template>
              <template v-else-if="column.key === 'dictCode'">
                <a-input
                  v-if="record.control === 'dict'"
                  v-model:value="record.dictCode"
                  placeholder="字典编码"
                  size="small"
                />
                <span v-else>-</span>
              </template>
              <template v-else-if="column.key === 'queryType'">
                <a-select v-model:value="record.queryType" style="width: 90px">
                  <a-select-option value="none">不查询</a-select-option>
                  <a-select-option value="eq">精确</a-select-option>
                  <a-select-option value="like">模糊</a-select-option>
                  <a-select-option value="range">范围</a-select-option>
                </a-select>
              </template>
              <template v-else-if="column.key === 'fieldAction'">
                <a class="danger-link" @click="removeField(index)">移除</a>
              </template>
            </template>
          </a-table>
          <a-button type="dashed" block style="margin-top: 8px" @click="addField">+ 添加字段</a-button>
        </a-form-item>

        <!-- 主从关系配置（可选）：一层主从，主表挂多从表 -->
        <a-divider style="margin: 4px 0 12px">主从关系（可选）</a-divider>
        <a-form-item>
          <a-checkbox v-model:checked="mdEnabled">作为主表挂从表（一层主从；从表需先保存为表设计，可现场快捷新建）</a-checkbox>
        </a-form-item>
        <template v-if="mdEnabled">
          <a-table
            :data-source="editForm.masterDetails"
            :columns="mdColumns"
            :pagination="false"
            row-key="_key"
            size="small"
            :scroll="{ x: 760 }"
          >
            <template #bodyCell="{ column, record, index }">
              <template v-if="column.key === 'detailTableId'">
                <a-select
                  v-model:value="record.detailTableId"
                  placeholder="选择从表设计"
                  style="width: 100%"
                  :options="detailOptions(record)"
                  :field-names="{ label: 'label', value: 'value' }"
                  show-search
                  option-filter-prop="label"
                />
              </template>
              <template v-else-if="column.key === 'foreignKey'">
                <a-select
                  v-if="detailFieldOptions(record).length"
                  v-model:value="record.foreignKey"
                  placeholder="外键字段"
                  style="width: 100%"
                  :options="detailFieldOptions(record)"
                />
                <a-input v-else v-model:value="record.foreignKey" placeholder="如 orderId" />
              </template>
              <template v-else-if="column.key === 'standalone'">
                <a-checkbox v-model:checked="record.standalone" />
              </template>
              <template v-else-if="column.key === 'dataSource'">
                <a-select v-model:value="record.dataSource" style="width: 100%">
                  <a-select-option value="free">自由录入</a-select-option>
                  <a-select-option value="dict">从字典挑</a-select-option>
                </a-select>
              </template>
              <template v-else-if="column.key === 'dictCode'">
                <a-input
                  v-if="record.dataSource === 'dict'"
                  v-model:value="record.dictCode"
                  placeholder="字典编码"
                  size="small"
                />
                <span v-else>-</span>
              </template>
              <template v-else-if="column.key === 'mdAction'">
                <a class="danger-link" @click="removeMdDetail(index)">移除</a>
              </template>
            </template>
          </a-table>
          <a-space style="margin-top: 8px; display: flex">
            <a-button type="dashed" @click="addMdDetail">+ 挂从表</a-button>
            <a-button type="dashed" @click="openQuickCreate">新建从表设计</a-button>
          </a-space>
        </template>
      </a-form>
    </a-modal>

    <!-- ============ 从表快捷创建弹窗 ============ -->
    <a-modal
      v-model:open="qcVisible"
      title="新建从表设计"
      width="720px"
      :confirm-loading="qcSaving"
      ok-text="保存从表设计"
      @ok="onQuickCreate"
    >
      <a-form layout="vertical">
        <a-row :gutter="16">
          <a-col :span="8">
            <a-form-item label="表名（英文下划线风格）" required>
              <a-input v-model:value="qcForm.tableName" placeholder="如 biz_contract_item" />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="中文名（业务名）" required>
              <a-input v-model:value="qcForm.displayName" placeholder="如 合同明细" />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="外键字段名（指向主表 Id）" required>
              <a-input v-model:value="qcForm.foreignKey" placeholder="如 contractId" />
            </a-form-item>
          </a-col>
        </a-row>
        <a-form-item label="业务字段（外键字段自动生成，可后续在设计器里继续完善）">
          <a-table
            :data-source="qcForm.fields"
            :columns="qcColumns"
            :pagination="false"
            row-key="_key"
            size="small"
          >
            <template #bodyCell="{ column, record, index }">
              <template v-if="column.key === 'name'">
                <a-input v-model:value="record.name" placeholder="英文" size="small" />
              </template>
              <template v-else-if="column.key === 'label'">
                <a-input v-model:value="record.label" placeholder="中文" size="small" />
              </template>
              <template v-else-if="column.key === 'dataType'">
                <a-select v-model:value="record.dataType" style="width: 110px" size="small">
                  <a-select-option value="string">string</a-select-option>
                  <a-select-option value="int">int</a-select-option>
                  <a-select-option value="long">long</a-select-option>
                  <a-select-option value="decimal">decimal</a-select-option>
                  <a-select-option value="datetime">datetime</a-select-option>
                  <a-select-option value="bool">bool</a-select-option>
                  <a-select-option value="text">text</a-select-option>
                </a-select>
              </template>
              <template v-else-if="column.key === 'nullable'">
                <a-checkbox v-model:checked="record.nullable" />
              </template>
              <template v-else-if="column.key === 'qcAction'">
                <a class="danger-link" @click="qcForm.fields.splice(index, 1)">移除</a>
              </template>
            </template>
          </a-table>
          <a-button type="dashed" block style="margin-top: 8px" @click="qcForm.fields.push(newField())">+ 添加字段</a-button>
        </a-form-item>
      </a-form>
    </a-modal>

    <!-- ============ 同步结果弹窗 ============ -->
    <a-modal v-model:open="syncVisible" title="同步建库结果" :footer="null" width="640px">
      <a-alert
        v-if="syncResult?.executed"
        type="success"
        show-icon
        message="变更已自动执行"
        style="margin-bottom: 12px"
      />
      <a-alert
        v-else
        type="info"
        show-icon
        message="结构与设计一致，无需变更"
        style="margin-bottom: 12px"
      />
      <div v-if="syncResult?.actions?.length" style="margin-bottom: 12px">
        <h4>已执行</h4>
        <ul class="sync-list">
          <li v-for="(a, i) in syncResult.actions" :key="i">{{ a }}</li>
        </ul>
      </div>
      <div v-if="syncResult?.destructivePending?.length">
        <a-alert type="warning" show-icon message="破坏性变更（删列/改类型）不会自动执行，请逐条确认" style="margin-bottom: 8px" />
        <div v-for="(sql, i) in syncResult.destructivePending" :key="i" class="sql-line">
          <code>{{ sql }}</code>
          <a-popconfirm title="该语句不可恢复，确定执行吗？" @confirm="onExecuteDestructive(sql)">
            <a-button size="small" danger style="margin-left: 8px">执行</a-button>
          </a-popconfirm>
        </div>
      </div>
    </a-modal>

    <!-- ============ 生成结果弹窗 ============ -->
    <a-modal v-model:open="genVisible" title="生成结果" :footer="null" width="860px">
      <a-alert
        type="success"
        show-icon
        :message="`已生成 ${genResult?.files?.length || 0} 个文件，耗时 ${genResult?.elapsedMs || 0} ms`"
        style="margin-bottom: 12px"
      />
      <!-- 编译验证（⑤）：生成文件写入源码根目录并编译，可选编译通过后重启 -->
      <div style="margin-bottom: 12px; display: flex; align-items: center; gap: 16px">
        <a-checkbox v-model:checked="buildRestart">编译通过后重启服务</a-checkbox>
        <a-button type="primary" :loading="buildVerifying" @click="onBuildVerify">编译验证</a-button>
      </div>
      <a-alert
        v-if="buildResult"
        :type="buildResult.success ? 'success' : 'error'"
        show-icon
        :message="buildResult.success ? `编译通过，耗时 ${buildResult.elapsedMs} ms${buildResult.restartOutput ? '，已执行重启命令' : ''}` : '编译失败，请查看下方输出'"
        style="margin-bottom: 12px"
      />
      <pre v-if="buildResult?.output" class="code-preview"><code>{{ buildResult.output }}</code></pre>
      <pre v-if="buildResult?.restartOutput" class="code-preview" style="margin-top: 8px"><code>{{ buildResult.restartOutput }}</code></pre>
      <a-collapse style="margin-top: 12px">
        <a-collapse-panel v-for="f in genResult?.files || []" :key="f.path" :header="f.path">
          <pre class="code-preview"><code>{{ f.content }}</code></pre>
        </a-collapse-panel>
      </a-collapse>
    </a-modal>

    <!-- ============ 服务脚手架弹窗（③） ============ -->
    <a-modal
      v-model:open="scaffoldVisible"
      title="生成新服务"
      :confirm-loading="scaffoldSaving"
      ok-text="生成"
      @ok="onScaffold"
    >
      <a-form layout="vertical">
        <a-row :gutter="16">
          <a-col :span="12">
            <a-form-item label="短名（Pascal 风格，如 Order）" required>
              <a-input v-model:value="scaffoldForm.shortName" placeholder="如 Order" />
            </a-form-item>
          </a-col>
          <a-col :span="12">
            <a-form-item label="中文名" required>
              <a-input v-model:value="scaffoldForm.displayName" placeholder="如 订单服务" />
            </a-form-item>
          </a-col>
        </a-row>
        <a-row :gutter="16">
          <a-col :span="12">
            <a-form-item label="路由前缀（留空 = 短名小写）">
              <a-input v-model:value="scaffoldForm.routePrefix" placeholder="留空自动" />
            </a-form-item>
          </a-col>
          <a-col :span="12">
            <a-form-item label="端口（留空自动分配）">
              <a-input-number v-model:value="scaffoldForm.port" :min="1" :max="65535" style="width: 100%" placeholder="留空自动" />
            </a-form-item>
          </a-col>
        </a-row>
      </a-form>
    </a-modal>

    <!-- ============ 新服务生成结果弹窗（③） ============ -->
    <a-modal v-model:open="scaffoldResultVisible" title="新服务生成结果" :footer="null" width="860px">
      <a-alert
        type="success"
        show-icon
        :message="`服务 ${scaffoldResult?.serviceName || ''} 已登记：端口 ${scaffoldResult?.port || ''}，路由前缀 ${scaffoldResult?.routePrefix || ''}；网关路由/部署/compose 登记片段见下方文件`"
        style="margin-bottom: 12px"
      />
      <a-collapse>
        <a-collapse-panel v-for="f in scaffoldResult?.files || []" :key="f.path" :header="f.path">
          <pre class="code-preview"><code>{{ f.content }}</code></pre>
        </a-collapse-panel>
      </a-collapse>
    </a-modal>

    <!-- ============ 生成记录详情弹窗 ============ -->
    <a-modal v-model:open="recordVisible" title="生成记录详情" :footer="null" width="860px">
      <a-collapse>
        <a-collapse-panel v-for="f in recordFiles" :key="f.path" :header="f.path">
          <pre class="code-preview"><code>{{ f.content }}</code></pre>
        </a-collapse-panel>
      </a-collapse>
    </a-modal>
  </PageLayout>
</template>

<script setup lang="ts">
import {
  getCodegenDesigns,
  getCodegenDesign,
  saveCodegenDesign,
  deleteCodegenDesign,
  syncCodegenDesign,
  executeDestructiveSql,
  generateCodegen,
  getCodegenRecords,
  getCodegenRecord,
  getCodegenServices,
  scaffoldCodegenService,
  buildVerifyCodegen,
  type CodegenDesign,
  type CodegenFieldDesign,
  type CodegenDetailTableConfig,
  type CodegenEnhancementConfig,
  type CodegenSyncResult,
  type CodegenGenerateResult,
  type CodegenRecord,
  type CodegenService,
  type CodegenServiceScaffoldResult,
  type CodegenBuildVerifyResult,
} from '@/api/codegen';
import { message } from 'ant-design-vue';
import { PlusOutlined } from '@ant-design/icons-vue';
import { ref, reactive, computed, watch, onMounted } from 'vue';
import FilterBar, { type FilterField } from '@/components/FilterBar/index.vue';
import PageLayout from '@/components/PageLayout/index.vue';

// 全量设计清单（主从配置里选从表用，不受列表分页影响）
const allDesigns = ref<CodegenDesign[]>([]);
async function loadAllDesigns() {
  const res = await getCodegenDesigns({ pageIndex: 1, pageSize: 500 });
  allDesigns.value = res.items;
}

// ============ 表设计列表 ============
const activeTab = ref('designs');
const designs = ref<CodegenDesign[]>([]);
const designLoading = ref(false);
const designFilters = reactive({ keyword: '' });
const designFilterFields: FilterField[] = [
  { key: 'keyword', label: '关键词', type: 'input', primary: true, placeholder: '搜索表名/中文名', width: 220 },
];

function resetDesignQuery() {
  designFilters.keyword = '';
  designPagination.current = 1;
  loadDesigns();
}
const designPagination = reactive({ current: 1, pageSize: 10, total: 0, showSizeChanger: true, showTotal: (t: number) => `共 ${t} 条` });

const designColumns = [
  { title: '表名', dataIndex: 'tableName', key: 'tableName' },
  { title: '中文名', dataIndex: 'displayName', key: 'displayName' },
  { title: '字段数', key: 'fieldCount', width: 90 },
  { title: '类型', key: 'mdType', width: 80 },
  { title: '同步状态', key: 'lastSyncAt', width: 110 },
  { title: '备注', dataIndex: 'remark', key: 'remark', ellipsis: true },
  { title: '操作', key: 'action', width: 240 },
];

async function loadDesigns() {
  designLoading.value = true;
  try {
    const res = await getCodegenDesigns({
      pageIndex: designPagination.current,
      pageSize: designPagination.pageSize,
      keyword: designFilters.keyword || undefined,
    });
    designs.value = res.items;
    designPagination.total = res.total;
  } finally {
    designLoading.value = false;
  }
}

function onDesignTableChange(p: any) {
  designPagination.current = p.current;
  designPagination.pageSize = p.pageSize;
  loadDesigns();
}

function parseFieldCount(fieldsJson?: string): number {
  if (!fieldsJson) return 0;
  try {
    return (JSON.parse(fieldsJson) as any[]).length;
  } catch {
    return 0;
  }
}

// ============ 表设计编辑 ============
const editVisible = ref(false);
const saving = ref(false);
interface MdDetailRow extends CodegenDetailTableConfig {
  _key: number;
}
const editForm = reactive<{
  id?: string;
  tableName: string;
  displayName: string;
  remark?: string;
  fields: CodegenFieldDesign[];
  masterDetails: MdDetailRow[];
  targetService?: string;
}>({
  tableName: '',
  displayName: '',
  fields: [],
  masterDetails: [],
});
const mdEnabled = ref(false);

let fieldKeySeq = 0;
function newField(): CodegenFieldDesign {
  return {
    name: '',
    label: '',
    dataType: 'string',
    length: 200,
    precision: undefined,
    nullable: true,
    isPrimary: false,
    isUnique: false,
    isIndex: false,
    showInList: true,
    control: 'text',
    queryType: 'none',
    _key: ++fieldKeySeq,
  } as any;
}

const fieldColumns = [
  { title: '字段名', key: 'name', width: 150 },
  { title: '显示名', key: 'label', width: 130 },
  { title: '类型', key: 'dataType', width: 120 },
  { title: '长度', key: 'length', width: 90 },
  { title: '小数位', key: 'precision', width: 80 },
  { title: '可空', key: 'nullable', width: 60 },
  { title: '列表显示', key: 'showInList', width: 80 },
  { title: '控件', key: 'control', width: 110 },
  { title: '字典编码', key: 'dictCode', width: 110 },
  { title: '查询', key: 'queryType', width: 100 },
  { title: '操作', key: 'fieldAction', width: 60 },
];

function addField() {
  editForm.fields.push(newField());
}

function removeField(index: number) {
  editForm.fields.splice(index, 1);
}

function openCreate() {
  editForm.id = undefined;
  editForm.tableName = '';
  editForm.displayName = '';
  editForm.remark = undefined;
  editForm.fields = [newField()];
  mdEnabled.value = false;
  editForm.masterDetails = [];
  editForm.targetService = undefined;
  editVisible.value = true;
}

async function openEdit(record: CodegenDesign) {
  const detail = await getCodegenDesign(record.id);
  editForm.id = detail.id;
  editForm.tableName = detail.tableName;
  editForm.displayName = detail.displayName;
  editForm.remark = detail.remark;
  editForm.targetService = detail.targetService || undefined;
  fieldKeySeq = 0;
  editForm.fields = parseFieldCount(detail.fieldsJson) > 0 ? JSON.parse(detail.fieldsJson!) : [];
  editForm.fields.forEach((f: any) => (f._key = ++fieldKeySeq));
  // 主从配置回填
  const md = detail.masterDetailJson ? safeParseMd(detail.masterDetailJson) : null;
  mdEnabled.value = !!(md && md.details.length);
  editForm.masterDetails = (md?.details || []).map((d) => ({ ...d, _key: ++fieldKeySeq }));
  await loadAllDesigns();
  editVisible.value = true;
}

function safeParseMd(json: string): { details: CodegenDetailTableConfig[] } | null {
  try {
    return JSON.parse(json);
  } catch {
    return null;
  }
}

async function onSaveDesign() {
  if (!editForm.tableName.trim()) {
    message.warning('请填写表名');
    return;
  }
  if (!editForm.displayName.trim()) {
    message.warning('请填写中文名');
    return;
  }
  if (!editForm.fields.length) {
    message.warning('请至少添加一个字段');
    return;
  }
  // 主从配置校验
  let masterDetail: CodegenDetailTableConfig[] | undefined;
  if (mdEnabled.value && editForm.masterDetails.length) {
    for (const d of editForm.masterDetails) {
      if (!d.detailTableId) {
        message.warning('主从配置里还有未选择从表设计的行');
        return;
      }
      if (!d.foreignKey.trim()) {
        message.warning('主从配置里还有未填写外键字段的行');
        return;
      }
      if (d.dataSource === 'dict' && !d.dictCode?.trim()) {
        message.warning('数据来源为「从字典挑」的从表需填写字典编码');
        return;
      }
    }
    masterDetail = editForm.masterDetails.map(({ _key, ...d }) => ({
      ...d,
      foreignKey: d.foreignKey.trim(),
      dictCode: d.dataSource === 'dict' ? d.dictCode?.trim() : undefined,
    }));
  }
  saving.value = true;
  try {
    await saveCodegenDesign({
      id: editForm.id,
      tableName: editForm.tableName.trim(),
      displayName: editForm.displayName.trim(),
      remark: editForm.remark,
      fields: editForm.fields.map(({ _key, ...f }: any) => f as CodegenFieldDesign),
      masterDetail: masterDetail?.length ? { details: masterDetail } : undefined,
      targetService: editForm.targetService || undefined,
    });
    message.success('保存成功');
    editVisible.value = false;
    await loadDesigns();
  } finally {
    saving.value = false;
  }
}

// ============ 主从关系配置 ============
const mdColumns = [
  { title: '从表设计', key: 'detailTableId', width: 200 },
  { title: '外键字段', key: 'foreignKey', width: 150 },
  { title: '独立功能', key: 'standalone', width: 80 },
  { title: '数据来源', key: 'dataSource', width: 110 },
  { title: '字典编码', key: 'dictCode', width: 120 },
  { title: '操作', key: 'mdAction', width: 60 },
];

function addMdDetail() {
  editForm.masterDetails.push({ detailTableId: '', foreignKey: '', standalone: false, dataSource: 'free', _key: ++fieldKeySeq });
}

function removeMdDetail(index: number) {
  editForm.masterDetails.splice(index, 1);
}

/** 可选从表：排除主表自己与已挂从表 */
function detailOptions(current: MdDetailRow) {
  return allDesigns.value
    .filter((d) => d.id !== editForm.id && !editForm.masterDetails.some((x) => x._key !== current._key && x.detailTableId === d.id))
    .map((d) => ({ label: `${d.displayName}（${d.tableName}）`, value: d.id }));
}

/** 选中从表后的外键字段候选（来自从表设计字段清单） */
function detailFieldOptions(current: MdDetailRow) {
  if (!current.detailTableId) return [];
  const design = allDesigns.value.find((d) => d.id === current.detailTableId);
  if (!design?.fieldsJson) return [];
  try {
    return (JSON.parse(design.fieldsJson) as any[]).map((f) => ({ label: `${f.label || f.name}（${f.name}）`, value: f.name }));
  } catch {
    return [];
  }
}

// ============ 从表快捷创建 ============
const qcVisible = ref(false);
const qcSaving = ref(false);
const qcForm = reactive<{ tableName: string; displayName: string; foreignKey: string; fields: CodegenFieldDesign[] }>({
  tableName: '',
  displayName: '',
  foreignKey: '',
  fields: [],
});
const qcColumns = [
  { title: '字段名', key: 'name', width: 160 },
  { title: '显示名', key: 'label', width: 150 },
  { title: '类型', key: 'dataType', width: 130 },
  { title: '可空', key: 'nullable', width: 70 },
  { title: '操作', key: 'qcAction', width: 60 },
];
let qcTargetKey: number | null = null;

function openQuickCreate() {
  qcForm.tableName = '';
  qcForm.displayName = '';
  qcForm.foreignKey = '';
  qcForm.fields = [newField()];
  // 目标行：最后一条配置行；没有则先补一条
  if (editForm.masterDetails.length) {
    qcTargetKey = editForm.masterDetails[editForm.masterDetails.length - 1]._key;
  } else {
    addMdDetail();
    qcTargetKey = editForm.masterDetails[0]._key;
  }
  qcVisible.value = true;
}

async function onQuickCreate() {
  if (!qcForm.tableName.trim()) {
    message.warning('请填写从表表名');
    return;
  }
  if (!qcForm.displayName.trim()) {
    message.warning('请填写从表中文名');
    return;
  }
  if (!qcForm.foreignKey.trim()) {
    message.warning('请填写外键字段名（从表上指向主表 Id 的字段）');
    return;
  }
  qcSaving.value = true;
  try {
    // 外键字段自动生成：long 非空 + 索引（同步建库时也会自动补索引标记）
    const fkField: CodegenFieldDesign = {
      name: qcForm.foreignKey.trim(),
      label: '主表ID',
      dataType: 'long',
      nullable: false,
      isPrimary: false,
      isUnique: false,
      isIndex: true,
      showInList: false,
      control: 'number',
      queryType: 'none',
    };
    const fields = [fkField, ...qcForm.fields.map(({ _key, ...f }: any) => f as CodegenFieldDesign)];
    await saveCodegenDesign({
      tableName: qcForm.tableName.trim(),
      displayName: qcForm.displayName.trim(),
      fields,
      remark: '从表（主从快捷创建）',
    });
    message.success('从表设计已保存，可在主从配置里选用');
    qcVisible.value = false;
    await loadAllDesigns();
    // 自动挂到发起快捷创建的配置行
    const created = allDesigns.value.find((d) => d.tableName === qcForm.tableName.trim());
    if (created && qcTargetKey != null) {
      const row = editForm.masterDetails.find((r) => r._key === qcTargetKey);
      if (row) {
        row.detailTableId = created.id;
        row.foreignKey = qcForm.foreignKey.trim();
      }
    }
  } finally {
    qcSaving.value = false;
  }
}

// ============ 同步建库 ============
const syncVisible = ref(false);
const syncResult = ref<CodegenSyncResult | null>(null);

async function onDeleteDesign(record: CodegenDesign) {
  await deleteCodegenDesign(record.id);
  message.success('删除成功');
  await loadDesigns();
}

async function onSync(record: CodegenDesign) {
  const res = await syncCodegenDesign(record.id);
  syncResult.value = res;
  syncVisible.value = true;
  await loadDesigns();
}

async function onExecuteDestructive(sql: string) {
  await executeDestructiveSql(sql);
  message.success('已执行');
  // 从待确认清单中移除已执行语句
  if (syncResult.value) {
    syncResult.value.destructivePending = syncResult.value.destructivePending.filter((s) => s !== sql);
  }
}

// ============ 一键生成 ============
const genVisible = ref(false);
const genResult = ref<CodegenGenerateResult | null>(null);

async function onGenerate(record: CodegenDesign) {
  const res = await generateCodegen(record.id);
  genResult.value = res;
  buildResult.value = null;
  buildRestart.value = false;
  genVisible.value = true;
}

// ============ 编译验证（⑤：生成文件写入源码根目录并编译，可选重启） ============
const buildRestart = ref(false);
const buildVerifying = ref(false);
const buildResult = ref<CodegenBuildVerifyResult | null>(null);

async function onBuildVerify() {
  if (!genResult.value?.recordId) return;
  buildVerifying.value = true;
  buildResult.value = null;
  try {
    buildResult.value = await buildVerifyCodegen(genResult.value.recordId, buildRestart.value);
    if (buildResult.value.success) message.success('编译通过');
    else message.error('编译失败，请查看输出');
  } finally {
    buildVerifying.value = false;
  }
}

// ============ 服务管理（③④：服务清单 + 一键生成新服务） ============
const services = ref<CodegenService[]>([]);
const servicesLoading = ref(false);

async function loadServices() {
  servicesLoading.value = true;
  try {
    services.value = await getCodegenServices();
  } finally {
    servicesLoading.value = false;
  }
}

const serviceColumns = [
  { title: '服务名', dataIndex: 'serviceName', key: 'serviceName' },
  { title: '中文名', dataIndex: 'displayName', key: 'displayName' },
  { title: '端口', dataIndex: 'port', key: 'port', width: 90 },
  { title: '路由前缀', dataIndex: 'routePrefix', key: 'routePrefix', width: 130 },
  { title: '类型', key: 'isBuiltIn', width: 90 },
];

/** 表设计弹窗的目标服务下拉数据源 */
const serviceOptions = computed(() =>
  services.value.map((s) => ({ label: `${s.displayName}（${s.serviceName}:${s.port}）`, value: s.serviceName })),
);

const scaffoldVisible = ref(false);
const scaffoldSaving = ref(false);
const scaffoldForm = reactive<{ shortName: string; displayName: string; routePrefix?: string; port?: number }>({
  shortName: '',
  displayName: '',
});

function openScaffold() {
  scaffoldForm.shortName = '';
  scaffoldForm.displayName = '';
  scaffoldForm.routePrefix = undefined;
  scaffoldForm.port = undefined;
  scaffoldVisible.value = true;
}

async function onScaffold() {
  if (!scaffoldForm.shortName.trim()) {
    message.warning('请填写服务短名');
    return;
  }
  if (!scaffoldForm.displayName.trim()) {
    message.warning('请填写中文名');
    return;
  }
  scaffoldSaving.value = true;
  try {
    scaffoldResult.value = await scaffoldCodegenService({
      shortName: scaffoldForm.shortName.trim(),
      displayName: scaffoldForm.displayName.trim(),
      routePrefix: scaffoldForm.routePrefix?.trim() || undefined,
      port: scaffoldForm.port || undefined,
    });
    scaffoldVisible.value = false;
    scaffoldResultVisible.value = true;
    await loadServices();
  } finally {
    scaffoldSaving.value = false;
  }
}

const scaffoldResultVisible = ref(false);
const scaffoldResult = ref<CodegenServiceScaffoldResult | null>(null);

// ============ 生成记录 ============
const records = ref<CodegenRecord[]>([]);
const recordLoading = ref(false);
const recordPagination = reactive({ current: 1, pageSize: 10, total: 0, showSizeChanger: true, showTotal: (t: number) => `共 ${t} 条` });

const recordColumns = [
  { title: '表名', dataIndex: 'tableName', key: 'tableName' },
  { title: '方式', key: 'mode', width: 90 },
  { title: '文件数', dataIndex: 'fileCount', key: 'fileCount', width: 90 },
  { title: '耗时', key: 'elapsedMs', width: 100 },
  { title: '时间', dataIndex: 'createdAt', key: 'createdAt', width: 180 },
  { title: '操作', key: 'action', width: 110 },
];

async function loadRecords() {
  recordLoading.value = true;
  try {
    const res = await getCodegenRecords({ pageIndex: recordPagination.current, pageSize: recordPagination.pageSize });
    records.value = res.items;
    recordPagination.total = res.total;
  } finally {
    recordLoading.value = false;
  }
}

function onRecordTableChange(p: any) {
  recordPagination.current = p.current;
  recordPagination.pageSize = p.pageSize;
  loadRecords();
}

const recordVisible = ref(false);
const recordFiles = ref<{ path: string; content: string }[]>([]);

async function openRecordDetail(record: CodegenRecord) {
  const detail = await getCodegenRecord(record.id);
  recordFiles.value = detail.files || [];
  recordVisible.value = true;
}

// ============ 增强配置（⑲补配即生效：字典关联/查询配置/导入导出） ============
const enhDesignId = ref<string>();
const enhDetail = ref<CodegenDesign | null>(null);
const enhFields = ref<(CodegenFieldDesign & { _key: number })[]>([]);
const enhImport = ref(false);
const enhExport = ref(false);
const enhSaving = ref(false);

const enhOptions = computed(() =>
  allDesigns.value.map((d) => ({ label: `${d.displayName}（${d.tableName}）`, value: d.id })),
);

const enhColumns = [
  { title: '字段名', dataIndex: 'name', key: 'name', width: 150 },
  { title: '显示名', dataIndex: 'label', key: 'label', width: 140 },
  { title: '控件', key: 'control', width: 100 },
  { title: '字典编码', key: 'dictCode', width: 160 },
  { title: '查询方式', key: 'queryType', width: 110 },
];

function controlLabel(control: string): string {
  const map: Record<string, string> = {
    text: '文本',
    number: '数字',
    date: '日期',
    textarea: '多行',
    dict: '字典',
    switch: '开关',
  };
  return map[control] || control;
}

function safeParseEnh(json: string): CodegenEnhancementConfig | null {
  try {
    return JSON.parse(json);
  } catch {
    return null;
  }
}

async function onEnhSelect(id: string) {
  const detail = await getCodegenDesign(id);
  enhDetail.value = detail;
  fieldKeySeq = 0;
  enhFields.value = (detail.fieldsJson && parseFieldCount(detail.fieldsJson) > 0 ? JSON.parse(detail.fieldsJson) : []).map(
    (f: any) => ({ ...f, _key: ++fieldKeySeq }),
  );
  const enh = detail.enhancementJson ? safeParseEnh(detail.enhancementJson) : null;
  enhImport.value = !!enh?.importEnabled;
  enhExport.value = !!enh?.exportEnabled;
}

async function onEnhSave() {
  const detail = enhDetail.value;
  if (!detail) return;
  // 字典控件字段必须带字典编码
  const missingDict = enhFields.value.find((f) => f.control === 'dict' && !f.dictCode?.trim());
  if (missingDict) {
    message.warning(`字段「${missingDict.label || missingDict.name}」为字典控件，请填写字典编码`);
    return;
  }
  enhSaving.value = true;
  try {
    // 主从配置原样回传（后端 SaveMasterDetailAsync 以请求为准，缺省即清空）
    const md = detail.masterDetailJson ? safeParseMd(detail.masterDetailJson) : null;
    await saveCodegenDesign({
      id: detail.id,
      tableName: detail.tableName,
      displayName: detail.displayName,
      remark: detail.remark,
      fields: enhFields.value.map(({ _key, ...f }: any) => f as CodegenFieldDesign),
      masterDetail: md?.details.length ? { details: md.details } : undefined,
      enhancement: { importEnabled: enhImport.value, exportEnabled: enhExport.value },
      targetService: detail.targetService || undefined,
    });
    message.success('增强配置已保存：生成页运行时读取配置，补配即生效');
  } finally {
    enhSaving.value = false;
  }
}

watch(activeTab, (tab) => {
  if (tab === 'enhance') loadAllDesigns();
  if (tab === 'services') loadServices();
});

onMounted(() => {
  loadDesigns();
  loadRecords();
  loadAllDesigns();
  loadServices();
});
</script>

<style scoped>
.sync-list {
  margin: 0;
  padding-left: 20px;
}
.sql-line {
  display: flex;
  align-items: center;
  margin-bottom: 6px;
}
.sql-line code {
  flex: 1;
  background: #f5f5f5;
  padding: 4px 8px;
  border-radius: 4px;
  font-size: 12px;
  word-break: break-all;
}
.code-preview {
  max-height: 420px;
  overflow: auto;
  background: #f5f5f5;
  padding: 8px;
  border-radius: 4px;
  font-size: 12px;
  margin: 0;
}
</style>
